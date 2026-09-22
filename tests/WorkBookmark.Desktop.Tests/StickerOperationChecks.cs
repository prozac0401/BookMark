using System.Diagnostics;
using System.Reflection;
using WorkBookmark.App;
using WorkBookmark.Core;
using WorkBookmark.Storage;

namespace WorkBookmark.Desktop.Tests;

// Runs the real click -> context -> isolated worker -> persisted result path.
// The child only exchanges protocol frames; it never opens a user's document.
internal static class StickerOperationChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string directory, Action<bool, string> assert)
    {
        Directory.CreateDirectory(directory);
        (UserSettings.Default with
        {
            DisplayMode = BookmarkDisplayMode.Stickers, IntroShown = true,
            CaptureHotkey = new Hotkey(7, (int)Keys.F15), RecentHotkey = new Hotkey(7, (int)Keys.F16)
        }).Save(directory);
        bool rejectNoteWrite = false;
        using var repository = new SqliteBookmarkRepository(Path.Combine(directory, "bookmarks.db"), operation =>
        {
            if (operation == "note" && rejectNoteWrite) throw new IOException("Synthetic note write failure");
        });
        var first = repository.UpsertCapture(new CapturedTarget(TargetKind.File, Path.Combine(directory, "first.txt"))).Bookmark;
        var second = repository.UpsertCapture(new CapturedTarget(TargetKind.File, Path.Combine(directory, "second.txt"))).Bookmark;
        string release = Path.Combine(directory, "release-worker");
        using var worker = new WorkerClient(Environment.ProcessPath!, ["--sticker-worker", release]);
        using var context = new BookmarkApplicationContext(repository, worker, directory);
        var manager = Field<object>(context, "_stickers");
        Form[] Forms() => ((System.Collections.IEnumerable)manager.GetType().GetProperty("Forms", Private)!.GetValue(manager)!).Cast<Form>().ToArray();
        Form For(Guid id) => Forms().Single(form => ((Bookmark)form.GetType().GetProperty("Bookmark")!.GetValue(form)!).Id == id);
        PumpUntil(() => Forms().Length == 2);
        var origin = For(first.Id);
        var sibling = For(second.Id);
        var originResume = Field<Button>(origin, "_resume");
        var siblingResume = Field<Button>(sibling, "_resume");
        var before = (siblingResume.Text, siblingResume.Enabled, siblingResume.Bounds, sibling.Bounds);
        originResume.PerformClick();
        PumpUntil(() => worker.IsBusy && !originResume.Enabled);
        assert(originResume.Text.Contains("처리 중", StringComparison.Ordinal) &&
            (siblingResume.Text, siblingResume.Enabled, siblingResume.Bounds, sibling.Bounds) == before &&
            Field<Button>(sibling, "_delete").Enabled && Field<RichTextBox>(sibling, "_note").Enabled,
            "SO01 resume click changes only the originating sticker; sibling actions and geometry remain unchanged");
        ClickNote(origin, MouseButtons.Left);
        ClickNote(sibling, MouseButtons.Right);
        assert(!Editing(origin) && !Editing(sibling) && !Field<bool>(context, "_openingNote"),
            "SO09 a busy sticker's note click and a right click cannot begin memo editing");
        var requestId = Field<Guid?>(context, "_activeRequest");
        siblingResume.PerformClick();
        Application.DoEvents();
        assert(Field<Guid?>(context, "_activeRequest") == requestId && Field<Guid?>(context, "_lastResumeId") == first.Id,
            "SO02 a second sticker click cannot queue or replace the active external operation");
        var fresh = repository.UpsertCapture(new CapturedTarget(TargetKind.File, Path.Combine(directory, "third.txt"))).Bookmark;
        Invoke(context, "PublishBookmark", fresh);
        assert(Field<Button>(For(fresh.Id), "_resume").Enabled && Field<Button>(For(fresh.Id), "_resume").Text == before.Text,
            "SO03 a sticker created during another bookmark's request does not inherit its progress state");
        File.WriteAllText(release, "complete");
        PumpUntil(() => !Field<bool>(context, "_operationInFlight"));
        assert(originResume.Enabled && originResume.Text == before.Text &&
            repository.Get(first.Id)!.LastResumeResult == ResultCode.PositionRestored &&
            repository.Get(second.Id)!.LastResumeResult is null && origin.Visible && sibling.Visible,
            "SO04 completion persists only the requested bookmark result and keeps stickers displayed");

        // A capture has no source sticker, so no sticker should advertise that it is opening.
        Guid captureId = Guid.NewGuid();
        assert((bool)Invoke(context, "BeginOperation", captureId, null)! && Forms().All(form => Field<Button>(form, "_resume").Enabled),
            "SO05 capture/background operations do not animate existing sticker buttons");
        Invoke(context, "EndOperation", captureId);

        ClickNote(origin, MouseButtons.Left);
        PumpUntil(() => Editing(origin));
        assert(!Editing(sibling) && Field<Form?>(context, "_note") is null && Field<TextBox>(origin, "_noteEditor").Text == "",
            "SO06 one click on an empty sticker memo starts a blank inline editor on that sticker only");
        var noteEditor = Field<TextBox>(origin, "_noteEditor");
        var bounds = origin.Bounds;
        noteEditor.Text = "이 스티커에서 저장할 다음 작업";
        rejectNoteWrite = true;
        Pump((Task)Invoke(origin, "SaveNoteAsync")!);
        assert(repository.Get(first.Id)!.Note == "" && noteEditor.Text == "이 스티커에서 저장할 다음 작업" &&
            (bool)origin.GetType().GetProperty("IsEditingNote")!.GetValue(origin)! && origin.Bounds == bounds,
            "SO07 an actual repository write failure keeps the inline draft at the same sticker without changing stored data");
        rejectNoteWrite = false;
        Pump((Task)Invoke(origin, "SaveNoteAsync")!);
        assert(repository.Get(first.Id)!.Note == "이 스티커에서 저장할 다음 작업" && repository.Get(second.Id)!.Note == "" &&
            !(bool)origin.GetType().GetProperty("IsEditingNote")!.GetValue(origin)! &&
            ((Bookmark)origin.GetType().GetProperty("Bookmark")!.GetValue(origin)!).Note == repository.Get(first.Id)!.Note,
            "SO08 retry commits the inline note only to its shared bookmark and returns to the sticker view");
        ClickNote(origin, MouseButtons.Left);
        PumpUntil(() => Editing(origin));
        assert(noteEditor.Text == repository.Get(first.Id)!.Note && noteEditor.Focused && origin.Bounds == bounds &&
            !Editing(sibling) && Field<Form?>(context, "_note") is null,
            "SO10 one click on existing memo text edits the saved text in place without another window");
    }

    internal static void RunWorker(string release)
    {
        var request = FrameProtocol.ReadAsync<WorkerRequest>(Console.OpenStandardInput(), CancellationToken.None).GetAwaiter().GetResult();
        var watch = Stopwatch.StartNew();
        while (!File.Exists(release) && watch.Elapsed < TimeSpan.FromSeconds(12)) Thread.Sleep(5);
        var response = new WorkerResponse(FrameProtocol.Version, request.RequestId,
            File.Exists(release) ? ResultCode.PositionRestored : ResultCode.ResumeOutcomeUnknown);
        FrameProtocol.WriteAsync(Console.OpenStandardOutput(), response, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static bool Editing(Form form) => (bool)form.GetType().GetProperty("IsEditingNote")!.GetValue(form)!;
    private static void ClickNote(Form form, MouseButtons button) => typeof(Control).GetMethod("OnMouseClick", Private)!
        .Invoke(Field<RichTextBox>(form, "_note"), [new MouseEventArgs(button, 1, 4, 4, 0)]);
    private static object? Invoke(object value, string name, params object?[] arguments) => value.GetType().GetMethod(name, Private)!.Invoke(value, arguments);
    private static void Pump(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(15)) { Application.DoEvents(); Thread.Sleep(2); }
        if (!ready()) throw new TimeoutException("Sticker request regression check timed out.");
    }
}
