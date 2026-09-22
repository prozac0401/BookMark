using System.Diagnostics;
using System.Reflection;
using WorkBookmark.App;
using WorkBookmark.Core;
using WorkBookmark.Storage;

namespace WorkBookmark.Desktop.Tests;

internal static class BookmarkRefreshChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string directory, Action<bool, string> assert)
    {
        Directory.CreateDirectory(directory);
        (UserSettings.Default with
        {
            CaptureHotkey = new Hotkey(7, (int)Keys.F15),
            RecentHotkey = new Hotkey(7, (int)Keys.F16),
            IntroShown = true
        }).Save(directory);
        using var repository = new SqliteBookmarkRepository(Path.Combine(directory, "bookmarks.db"));
        var target = new CapturedTarget(TargetKind.File, Path.Combine(directory, "새로 고침 순서 검증.txt"));
        var bookmark = repository.UpsertCapture(target).Bookmark;
        using var worker = new WorkerClient();
        using var context = new BookmarkApplicationContext(repository, worker, directory);
        object manager = Field<object>(context, "_stickers");
        Form[] Forms() => ((System.Collections.IEnumerable)manager.GetType().GetProperty("Forms", Private)!.GetValue(manager)!).Cast<Form>().ToArray();
        Application.DoEvents();
        Pump((Task)Invoke(manager, "SetEnabledAsync", true, false)!);

        SynchronizationContext? previous = SynchronizationContext.Current;
        var queued = new QueuedContext();
        try
        {
            SynchronizationContext.SetSynchronizationContext(queued);
            Task<Bookmark?> oldActive = Refresh(context, bookmark.Id);
            queued.WaitForCount(1); // The active snapshot was read; its UI continuation is held.
            repository.SoftDelete(bookmark.Id);
            SetField(context, "_undoId", (Guid?)bookmark.Id);
            SetField(context, "_undoUntil", DateTimeOffset.UtcNow.AddMinutes(1));
            Task<Bookmark?> newDeleted = Refresh(context, bookmark.Id);
            queued.WaitForCount(2);
            queued.RunLast();
            assert(newDeleted.IsCompletedSuccessfully && newDeleted.Result?.DeletedAtUtc is not null && Forms().Length == 0,
                "RF01 authoritative deleted snapshot removes the matching sticker");
            queued.RunFirst();
            assert(oldActive.IsCompletedSuccessfully && oldActive.Result is null && Forms().Length == 0 &&
                Field<Guid?>(context, "_undoId") == bookmark.Id && repository.Get(bookmark.Id)!.DeletedAtUtc is not null,
                "RF02 late active snapshot cannot recreate a deleted sticker or discard its undo");

            Task<Bookmark?> oldDeleted = Refresh(context, bookmark.Id);
            queued.WaitForCount(1);
            repository.UpsertCapture(target);
            repository.UpdateNote(bookmark.Id, "가장 최근에 저장한 메모");
            Task<Bookmark?> newActive = Refresh(context, bookmark.Id);
            queued.WaitForCount(2);
            queued.RunLast();
            queued.RunFirst();
            var current = (Bookmark)Forms().Single().GetType().GetProperty("Bookmark")!.GetValue(Forms().Single())!;
            assert(newActive.IsCompletedSuccessfully && oldDeleted.IsCompletedSuccessfully && oldDeleted.Result is null &&
                current.DeletedAtUtc is null && current.Note == "가장 최근에 저장한 메모" && Field<Guid?>(context, "_undoId") is null,
                "RF03 late deleted snapshot cannot remove a recaptured bookmark or restore stale undo");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        using var editor = new Form { Text = "합성 메모 편집기" };
        var unsaved = new TextBox { Text = "아직 저장하지 않은 메모", Dock = DockStyle.Fill };
        editor.Controls.Add(unsaved);
        Forms().Single().TopMost = true;
        Invoke(context, "ShowAuxiliary", editor);
        assert(editor.Owner is null && editor.TopMost,
            "RF04 modeless editor stays above pinned stickers without owning a disposable bookmark view");
        Invoke(manager, "Remove", bookmark.Id);
        assert(!editor.IsDisposed && editor.Visible && unsaved.Text == "아직 저장하지 않은 메모",
            "RF05 deleting a sticker preserves an open editor and its unsaved input");
    }

    private static Task<Bookmark?> Refresh(object context, Guid id)
    {
        // Keep the worker from completing before the async method captures our context.
        lock (Field<object>(context, "_repositoryLock"))
            return (Task<Bookmark?>)Invoke(context, "RefreshBookmarkAsync", id)!;
    }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static void SetField(object value, string name, object? replacement) => value.GetType().GetField(name, Private)!.SetValue(value, replacement);
    private static object? Invoke(object value, string name, params object[] arguments) => value.GetType().GetMethod(name, Private)!.Invoke(value, arguments);
    private static void Pump(Task task)
    {
        var deadline = Stopwatch.StartNew();
        while (!task.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(15)) { Application.DoEvents(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("Bookmark refresh setup timed out.");
        task.GetAwaiter().GetResult();
    }

    // Reorders completed reads' UI continuations while keeping all real database reads,
    // generation checks and WinForms updates. Production needs no timing/test hook.
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly List<(SendOrPostCallback Callback, object? State)> _pending = [];
        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_pending) { _pending.Add((callback, state)); Monitor.PulseAll(_pending); }
        }
        internal void WaitForCount(int count)
        {
            var elapsed = Stopwatch.StartNew();
            lock (_pending)
                while (_pending.Count < count)
                {
                    TimeSpan remaining = TimeSpan.FromSeconds(10) - elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero || !Monitor.Wait(_pending, remaining))
                        throw new TimeoutException("Bookmark read did not queue its UI continuation.");
                }
        }
        internal void RunFirst() => Run(false);
        internal void RunLast() => Run(true);
        private void Run(bool last)
        {
            (SendOrPostCallback Callback, object? State) item;
            lock (_pending)
            {
                int index = last ? _pending.Count - 1 : 0;
                item = _pending[index];
                _pending.RemoveAt(index);
            }
            item.Callback(item.State);
        }
    }
}
