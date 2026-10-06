using System.Diagnostics;
using System.Reflection;
using WorkBookmark.App;
using WorkBookmark.Core;
using WorkBookmark.Storage;

namespace WorkBookmark.Desktop.Tests;

internal static class NoteReadVisibilityChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string directory, Action<bool, string> assert)
    {
        LateReadAfterHide(Path.Combine(directory, "late-read"), assert);
        NewRequestWhileOldReadWaits(Path.Combine(directory, "new-request"), assert);
    }

    private static void LateReadAfterHide(string directory, Action<bool, string> assert)
    {
        using var fixture = new Fixture(directory);
        Form form = fixture.FormFor(fixture.First.Id);
        int activations = 0, reveals = 0;
        form.Activated += (_, _) => activations++;
        form.VisibleChanged += (_, _) => { if (form.Visible) reveals++; };
        using var read = fixture.Repository.GateNextRead();
        Task opening = fixture.Edit(fixture.First.Id);
        read.WaitUntilEntered();
        assert(!opening.IsCompleted && fixture.Forms.All(value => !Editing(value)),
            "NR01 the real application note-open route waits at the controlled repository read before editing");
        fixture.HideAll();
        activations = reveals = 0;
        read.Complete();
        Pump(opening);
        Console.WriteLine($"OBSERVE NR02: visible={form.Visible}, editing={Editing(form)}, activations={activations}, reveals={reveals}");
        assert(fixture.Forms.All(value => !value.Visible && !Editing(value)) && activations == 0 && reveals == 0,
            "NR02 a note read completing after HideAll cannot reveal, activate or edit any sticker");

        using var freshRead = fixture.Repository.GateNextRead();
        Task freshOpening = fixture.Edit(fixture.First.Id);
        freshRead.WaitUntilEntered();
        assert(!freshOpening.IsCompleted && fixture.Forms.All(value => !value.Visible),
            "NR03 a new note request after hide waits for its own controlled read");
        freshRead.Complete();
        Pump(freshOpening);
        assert(form.Visible && Editing(form) && reveals == 1 &&
            fixture.Forms.Where(value => value != form).All(value => !value.Visible) &&
            Field<TextBox>(form, "_noteEditor").Text == "stored first note",
            "NR04 a fresh request after the cancelled read opens the requested note and keeps other stickers hidden");
    }

    private static void NewRequestWhileOldReadWaits(string directory, Action<bool, string> assert)
    {
        using var fixture = new Fixture(directory);
        using var oldRead = fixture.Repository.GateNextRead();
        Task oldOpening = fixture.Edit(fixture.First.Id);
        oldRead.WaitUntilEntered();
        fixture.HideAll();
        using var newRead = fixture.Repository.GateNextRead();
        Task newOpening = fixture.Edit(fixture.Second.Id);
        assert(!newOpening.IsCompleted,
            "NR05 a fresh request after hide is accepted while the cancelled note read is still pending");
        oldRead.Complete();
        newRead.WaitUntilEntered();
        Pump(oldOpening);
        assert(!newOpening.IsCompleted && Field<bool>(fixture.Context, "_openingNote") &&
            fixture.Forms.All(value => !value.Visible && !Editing(value)),
            "NR06 the cancelled read's completion keeps the newer opening guard and every sticker hidden");
        Task duplicate = fixture.Edit(fixture.First.Id);
        assert(duplicate.IsCompletedSuccessfully && !newOpening.IsCompleted,
            "NR07 the newer pending request still suppresses duplicate note opens in the same visibility generation");
        newRead.Complete();
        Pump(newOpening);
        Form requested = fixture.FormFor(fixture.Second.Id);
        assert(requested.Visible && Editing(requested) && !fixture.FormFor(fixture.First.Id).Visible &&
            !Field<bool>(fixture.Context, "_openingNote") &&
            Field<TextBox>(requested, "_noteEditor").Text == "stored second note",
            "NR08 the request made after hide opens its own note when its read completes");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly GatedRepository Repository;
        internal readonly WorkerClient Worker = new();
        internal readonly BookmarkApplicationContext Context;
        internal readonly Bookmark First, Second;
        private readonly object _manager;

        internal Fixture(string directory)
        {
            Directory.CreateDirectory(directory);
            (UserSettings.Default with
            {
                CaptureHotkey = new Hotkey(7, (int)Keys.F15), RecentHotkey = new Hotkey(7, (int)Keys.F16),
                DisplayMode = BookmarkDisplayMode.Stickers, IntroShown = true,
                StickerPresentationVersion = UserSettings.CurrentStickerPresentationVersion
            }).Save(directory);
            Repository = new GatedRepository(new SqliteBookmarkRepository(Path.Combine(directory, "bookmarks.db")));
            First = Repository.UpsertCapture(new CapturedTarget(TargetKind.File, Path.Combine(directory, "first.txt"))).Bookmark;
            Second = Repository.UpsertCapture(new CapturedTarget(TargetKind.File, Path.Combine(directory, "second.txt"))).Bookmark;
            Repository.UpdateNote(First.Id, "stored first note");
            Repository.UpdateNote(Second.Id, "stored second note");
            Context = new BookmarkApplicationContext(Repository, Worker, directory);
            _manager = Field<object>(Context, "_stickers");
            PumpUntil(() => Forms.Length == 2 && Forms.All(form => form.Visible));
        }

        internal Form[] Forms => ((System.Collections.IEnumerable)_manager.GetType().GetProperty("Forms", Private)!.GetValue(_manager)!).Cast<Form>().ToArray();
        internal Form FormFor(Guid id) => Forms.Single(form => ((Bookmark)form.GetType().GetProperty("Bookmark")!.GetValue(form)!).Id == id);
        internal Task Edit(Guid id) => (Task)Invoke(Context, "EditNoteAsync", id)!;
        internal void HideAll() => Invoke(_manager, "HideAll");
        public void Dispose()
        {
            Context.ExitThread();
            Context.Dispose();
            Worker.Dispose();
            Repository.Dispose();
        }
    }

    // Block only the selected Get, inside the real application's ReadAsync path.
    // The event establishes read entry and the completion source explicitly
    // releases it: elapsed time never decides whether HideAll wins the race.
    private sealed class ReadGate : IDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Read() { _entered.SetResult(); _completion.Task.GetAwaiter().GetResult(); }
        internal void WaitUntilEntered()
        {
            if (!_entered.Task.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Controlled note read was not entered.");
        }
        internal void Complete() => _completion.TrySetResult();
        public void Dispose() => Complete();
    }

    private sealed class GatedRepository(IBookmarkRepository inner) : IBookmarkRepository
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<ReadGate> _reads = new();
        internal ReadGate GateNextRead() { var gate = new ReadGate(); _reads.Enqueue(gate); return gate; }
        public Bookmark? Get(Guid id) { if (_reads.TryDequeue(out var gate)) gate.Read(); return inner.Get(id); }
        public CaptureCommit UpsertCapture(CapturedTarget target) => inner.UpsertCapture(target);
        public SearchResults List(string query = "") => inner.List(query);
        public IReadOnlyList<Bookmark> ListActive() => inner.ListActive();
        public IReadOnlyList<Bookmark> ListDeleted(int limit = 100) => inner.ListDeleted(limit);
        public IReadOnlyList<StickerLayout> GetStickerLayouts() => inner.GetStickerLayouts();
        public void SaveStickerLayout(StickerLayout layout) => inner.SaveStickerLayout(layout);
        public void UpdateNote(Guid id, string note) => inner.UpdateNote(id, note);
        public void SoftDelete(Guid id) => inner.SoftDelete(id);
        public void Restore(Guid id) => inner.Restore(id);
        public void RecordResume(Guid id, ResultCode result) => inner.RecordResume(id, result);
        public void Relink(Guid id, CapturedTarget target) => inner.Relink(id, target);
        public void Dispose() => inner.Dispose();
    }

    private static bool Editing(Form form) => (bool)form.GetType().GetProperty("IsEditingNote")!.GetValue(form)!;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static object? Invoke(object value, string name, params object[] arguments) => value.GetType().GetMethod(name, Private)!.Invoke(value, arguments);
    private static void Pump(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Yield(); }
        if (!ready()) throw new TimeoutException("Note read visibility check timed out.");
    }
}
