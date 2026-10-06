using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using WorkBookmark.App;
using WorkBookmark.Core;
using WorkBookmark.Storage;

namespace WorkBookmark.Desktop.Tests;

internal static class StickerVisibilityChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string directory, Action<bool, string> assert)
    {
        HotkeyFlow(directory, assert);
        PendingReveal(assert);
        EmptyAndFailedReveal(assert);
        HiddenNoteSave(assert);
        CancelledArrangement(assert);
    }

    private static void HotkeyFlow(string directory, Action<bool, string> assert)
    {
        Directory.CreateDirectory(directory);
        var settings = UserSettings.Default with
        {
            CaptureHotkey = new Hotkey(7, (int)Keys.F21), RecentHotkey = new Hotkey(7, (int)Keys.F22),
            DisplayMode = BookmarkDisplayMode.Stickers, IntroShown = true,
            StickerPresentationVersion = UserSettings.CurrentStickerPresentationVersion
        };
        settings.Save(directory);
        using var repository = new SqliteBookmarkRepository(Path.Combine(directory, "bookmarks.db"));
        Bookmark Capture(string name) => repository.UpsertCapture(new CapturedTarget(TargetKind.File,
            Path.Combine(directory, name + ".txt"))).Bookmark;
        var first = Capture("toggle-first");
        Capture("toggle-second");
        repository.UpdateNote(first.Id, "토글 전후 유지할 메모");
        using (var worker = new WorkerClient())
        using (var context = new BookmarkApplicationContext(repository, worker, directory))
        {
            try
            {
                object manager = Field<object>(context, "_stickers");
                PumpUntil(() => Forms(manager).Length == 2 && Forms(manager).All(form => form.Visible));
                Form origin = Forms(manager).Single(form => BookmarkFor(form).Id == first.Id);
                origin.Location += new Size(-20, 20);
                Field<ToolStripMenuItem>(origin, "_collapseMenu").PerformClick();
                var bounds = origin.Bounds;
                Pump(Call(manager, "SavePendingAsync"));
                var layout = repository.GetStickerLayouts().Single(value => value.BookmarkId == first.Id);
                PressViewHotkey(context);
                assert(Forms(manager).All(form => !form.Visible) && repository.ListActive().Count == 2,
                    "SV01 the registered custom view hotkey hides visible stickers without deleting bookmarks");
                var added = Capture("captured-while-hidden");
                Invoke(context, "PublishBookmark", added);
                assert(Forms(manager).Length == 3 && Forms(manager).All(form => !form.Visible),
                    "SV02 a capture after hotkey hide stays hidden until the next reveal");
                PressViewHotkey(context);
                PumpUntil(() => Forms(manager).All(form => form.Visible));
                assert(ReferenceEquals(origin, Forms(manager).Single(form => BookmarkFor(form).Id == first.Id)) &&
                    origin.Bounds == bounds && (bool)origin.GetType().GetProperty("IsCollapsed")!.GetValue(origin)! &&
                    BookmarkFor(origin).Note == "토글 전후 유지할 메모" &&
                    repository.GetStickerLayouts().Single(value => value.BookmarkId == first.Id) == layout,
                    "SV03 the next hotkey restores every sticker with its identity, position, collapse state and note intact");
                origin.Close();
                PressViewHotkey(context);
                assert(Forms(manager).All(form => !form.Visible),
                    "SV04 one individually hidden sticker does not prevent the hotkey from hiding all remaining visible stickers");
                PressViewHotkey(context);
                PumpUntil(() => Forms(manager).All(form => form.Visible));
                foreach (var form in Forms(manager)) form.Close();
                PressViewHotkey(context);
                PumpUntil(() => Forms(manager).All(form => form.Visible));
                assert(Forms(manager).Length == 3,
                    "SV05 closing every sticker individually makes the next view hotkey reveal them all");
                context.ShowBookmarks();
                PumpUntil(() => !Field<bool>(manager, "_showPending"));
                assert(Forms(manager).All(form => form.Visible),
                    "SV06 explicit show remains reveal-only when stickers are already visible");
                assert(!worker.IsBusy && repository.ListActive().All(bookmark => bookmark.LastResumeAtUtc is null),
                    "SV07 toggling visibility never opens a bookmarked target");
            }
            finally { context.ExitThread(); }
        }

        (settings with { DisplayMode = BookmarkDisplayMode.List }).Save(directory);
        using var listWorker = new WorkerClient();
        using var listContext = new BookmarkApplicationContext(repository, listWorker, directory);
        try
        {
            var recent = Field<Form>(listContext, "_recent");
            PressViewHotkey(listContext);
            PumpUntil(() => recent.Visible);
            PressViewHotkey(listContext);
            Application.DoEvents();
            assert(recent.Visible && Forms(Field<object>(listContext, "_stickers")).Length == 0,
                "SV08 the same custom hotkey continues to open and keep the list visible in list mode");
        }
        finally { listContext.ExitThread(); }
    }

    private static void PendingReveal(Action<bool, string> assert)
    {
        using var fixture = new GatedManager();
        Task startup = Call(fixture.Manager, "SetEnabledAsync", true, false);
        Pump(Call(fixture.Manager, "ToggleVisibilityAsync"));
        fixture.Loads[0].SetException(new IOException("Cancelled reveal failed late"));
        Pump(startup);
        assert(Forms(fixture.Manager).Length == 0 && fixture.Notifications.Count == 0,
            "SV09 a second press cancels a pending startup reveal and suppresses its late failure");

        Task oldReveal = Call(fixture.Manager, "ToggleVisibilityAsync");
        Pump(Call(fixture.Manager, "ToggleVisibilityAsync"));
        Task newReveal = Call(fixture.Manager, "ToggleVisibilityAsync");
        fixture.Complete(1);
        Pump(oldReveal);
        assert(Forms(fixture.Manager).Length == 0 && !newReveal.IsCompleted,
            "SV10 a cancelled load completing during the next reveal cannot display stale stickers");
        Pump(Call(fixture.Manager, "ToggleVisibilityAsync"));
        fixture.Complete(2);
        Pump(newReveal);
        assert(fixture.Loads.Count == 3 && Forms(fixture.Manager).Length == 0,
            "SV11 late completion of the old load cannot clear the new pending reveal and turn hide into another load");
        Task finalReveal = Call(fixture.Manager, "ToggleVisibilityAsync");
        fixture.Complete(3);
        Pump(finalReveal);
        assert(Forms(fixture.Manager).Single().Visible,
            "SV12 the next press reveals stickers normally after rapid load cancellations");
    }

    private static void EmptyAndFailedReveal(Action<bool, string> assert)
    {
        using var fixture = new GatedManager();
        Task startup = Call(fixture.Manager, "SetEnabledAsync", true, false);
        fixture.Loads[0].SetResult(([], []));
        Pump(startup);
        Task empty = Call(fixture.Manager, "ToggleVisibilityAsync");
        fixture.Loads[1].SetResult(([], []));
        Pump(empty);
        Task failed = Call(fixture.Manager, "ToggleVisibilityAsync");
        fixture.Loads[2].SetException(new IOException("Synthetic reveal failure"));
        Pump(failed);
        assert(fixture.Notifications.Count == 2 && Forms(fixture.Manager).Length == 0,
            "SV13 an empty reveal and a failed reveal each finish and report their result");
        Task retry = Call(fixture.Manager, "ToggleVisibilityAsync");
        fixture.Complete(3);
        Pump(retry);
        assert(Forms(fixture.Manager).Single().Visible && fixture.Loads.Count == 4,
            "SV14 the next hotkey retries after empty or failed loads without an extra hide press");
    }

    private static void HiddenNoteSave(Action<bool, string> assert)
    {
        using var fixture = new GatedManager();
        Task startup = Call(fixture.Manager, "SetEnabledAsync", true, false);
        fixture.Complete(0);
        Pump(startup);
        Invoke(fixture.Manager, "HideAll");
        var save = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Invoke(fixture.Manager, "EditNote", fixture.Bookmark, (Func<string, Task>)(_ => save.Task));
        Form form = Forms(fixture.Manager).Single();
        var editor = Field<TextBox>(form, "_noteEditor");
        editor.Text = "숨겨도 유지할 저장 실패 초안";
        Task saving = Call(form, "SaveNoteAsync");
        Pump(Call(fixture.Manager, "ToggleVisibilityAsync"));
        assert(!form.Visible && fixture.Loads.Count == 1,
            "SV15 a note opened while globally hidden is still a visible sticker and the hotkey hides it during save");
        save.SetException(new IOException("Synthetic note failure"));
        Pump(saving);
        assert(!form.Visible && editor.Text == "숨겨도 유지할 저장 실패 초안" &&
            (bool)form.GetType().GetProperty("IsEditingNote")!.GetValue(form)!,
            "SV16 late note-save failure retains its draft without revealing a hidden sticker");
        Task reveal = Call(fixture.Manager, "ToggleVisibilityAsync");
        fixture.Complete(1);
        Pump(reveal);
        assert(form.Visible && editor.Text == "숨겨도 유지할 저장 실패 초안",
            "SV17 revealing after a failed hidden save preserves the pending note draft");
    }

    private static void CancelledArrangement(Action<bool, string> assert)
    {
        using var fixture = new GatedManager();
        Task startup = Call(fixture.Manager, "SetEnabledAsync", true, false);
        fixture.Complete(0);
        Pump(startup);
        Form form = Forms(fixture.Manager).Single();
        var area = Screen.FromControl(form).WorkingArea;
        form.Location = new Point(area.Left + Math.Min(80, Math.Max(0, area.Width - form.Width)),
            area.Top + Math.Min(80, Math.Max(0, area.Height - form.Height)));
        Invoke(form, "OnResizeEnd", EventArgs.Empty);
        Flush(fixture.Manager);
        var original = form.Bounds;
        int writes = fixture.SavedBatches.Count;
        var modeType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.StickerArrangementMode")!;
        Task Arrange(string mode) => Call(fixture.Manager, "ArrangeAsync", Enum.Parse(modeType, mode));
        bool PlacementUnchanged() => form.Bounds == original && fixture.SavedBatches.Count == writes;

        Task hidden = Arrange("Horizontal");
        Invoke(fixture.Manager, "HideAll");
        fixture.Complete(1);
        Pump(hidden);
        Flush(fixture.Manager);
        assert(!form.Visible && PlacementUnchanged(),
            "SV18 hiding during an arrangement load cancels its late reveal, movement and layout writes");

        Task superseded = Arrange("Vertical");
        Invoke(fixture.Manager, "HideAll");
        Task newerReveal = Call(fixture.Manager, "ToggleVisibilityAsync");
        fixture.Complete(2);
        Pump(superseded);
        assert(!newerReveal.IsCompleted && !form.Visible && PlacementUnchanged(),
            "SV19 a cancelled arrangement cannot move hidden stickers while a newer reveal is loading");
        Pump(Call(fixture.Manager, "ToggleVisibilityAsync"));
        fixture.Complete(3);
        Pump(newerReveal);
        Flush(fixture.Manager);
        assert(fixture.Loads.Count == 4 && !form.Visible && PlacementUnchanged(),
            "SV20 a late arrangement completion retains the newer pending toggle so the next press cancels it");

        Task failed = Arrange("Grid");
        fixture.Loads[4].SetException(new IOException("Synthetic arrangement load failure"));
        Pump(failed);
        Flush(fixture.Manager);
        assert(!form.Visible && PlacementUnchanged() && fixture.Notifications.Count == 1,
            "SV21 failed arrangement loading reports once and preserves existing positions and stored layouts");

        Task stale = Arrange("Horizontal");
        Task explicitReveal = Call(fixture.Manager, "ShowAllAsync", false);
        fixture.Complete(6);
        Pump(explicitReveal);
        fixture.Complete(5);
        Pump(stale);
        Flush(fixture.Manager);
        assert(form.Visible && PlacementUnchanged(),
            "SV22 an old arrangement cannot reposition or rewrite stickers after a newer explicit reveal completes");

        Task disabled = Arrange("Vertical");
        Pump(Call(fixture.Manager, "SetEnabledAsync", false, false));
        fixture.Complete(7);
        Pump(disabled);
        Flush(fixture.Manager);
        assert(!form.Visible && PlacementUnchanged(),
            "SV23 switching to list mode during an arrangement load prevents late placement changes");
    }

    private sealed class GatedManager : IDisposable
    {
        internal readonly List<TaskCompletionSource<(IReadOnlyList<Bookmark> Items, IReadOnlyList<StickerLayout> Layouts)>> Loads = [];
        internal readonly List<string> Notifications = [];
        internal readonly List<StickerLayout[]> SavedBatches = [];
        internal readonly object Manager;
        internal readonly Bookmark Bookmark;

        internal GatedManager()
        {
            var now = DateTimeOffset.UtcNow;
            var target = new CapturedTarget(TargetKind.File, @"C:\synthetic\visibility.txt");
            Bookmark = new(Guid.NewGuid(), target, target.Path, "합성 토글 검사", "", now, now, 1, null, null, null, null);
            Func<Task<(IReadOnlyList<Bookmark> Items, IReadOnlyList<StickerLayout> Layouts)>> load = () =>
            {
                var completion = new TaskCompletionSource<(IReadOnlyList<Bookmark>, IReadOnlyList<StickerLayout>)>(TaskCreationOptions.RunContinuationsAsynchronously);
                Loads.Add(completion);
                return completion.Task;
            };
            Manager = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.StickerManager")!
                .GetConstructors(Private).Single().Invoke([load, (Action<IReadOnlyList<StickerLayout>>)(batch => SavedBatches.Add(batch.ToArray())), (Action<string>)Notifications.Add]);
        }

        internal void Complete(int index) => Loads[index].SetResult(([Bookmark], []));
        public void Dispose() => ((IDisposable)Manager).Dispose();
    }

    private static void PressViewHotkey(BookmarkApplicationContext context)
    {
        var hotkeys = Field<HotkeyWindow>(context, "_hotkeys");
        var registration = Field<Dictionary<bool, (int Id, Hotkey Key)>>(hotkeys, "_registered")[false];
        if (registration.Key != new Hotkey(7, (int)Keys.F22)) throw new InvalidOperationException("The custom view hotkey was not registered.");
        SendMessage(hotkeys.Handle, 0x0312, (nint)registration.Id, nint.Zero);
    }

    private static Form[] Forms(object manager) => ((System.Collections.IEnumerable)manager.GetType().GetProperty("Forms", Private)!.GetValue(manager)!).Cast<Form>().ToArray();
    private static Bookmark BookmarkFor(Form form) => (Bookmark)form.GetType().GetProperty("Bookmark")!.GetValue(form)!;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static object? Invoke(object value, string name, params object[] arguments) => value.GetType().GetMethod(name, Private)!.Invoke(value, arguments);
    private static Task Call(object value, string name, params object[] arguments) => (Task)Invoke(value, name, arguments)!;
    private static void Flush(object manager)
    {
        PumpUntil(() => Field<Task?>(manager, "_writeTask") is null);
        Pump(Call(manager, "SavePendingAsync"));
    }
    private static void Pump(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Sleep(1); }
        if (!ready()) throw new TimeoutException("Sticker visibility check timed out.");
    }

    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wparam, nint lparam);
}
