using System.Drawing;
using System.Reflection;
using WorkBookmark.App;
using WorkBookmark.Core;
using WorkBookmark.Storage;

namespace WorkBookmark.Desktop.Tests;

internal static class StickerIntegrationChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(string directory, Action<bool, string> assert)
    {
        Directory.CreateDirectory(directory);
        var settings = UserSettings.Default with { CaptureHotkey = new Hotkey(7, (int)Keys.F15), RecentHotkey = new Hotkey(7, (int)Keys.F16), IntroShown = true };
        settings.Save(directory);
        bool failDelete = false;
        using var repository = new SqliteBookmarkRepository(Path.Combine(directory, "bookmarks.db"), operation =>
        {
            if (operation == "delete" && failDelete) throw new IOException("Injected deletion failure");
        });
        var values = Enumerable.Range(0, 25).Select(index => repository.UpsertCapture(new CapturedTarget(TargetKind.File,
            Path.Combine(directory, $"합성 문서 {index:00}.txt"))).Bookmark).ToArray();
        using var worker = new WorkerClient();
        using var context = new BookmarkApplicationContext(repository, worker, directory);
        object manager = Field<object>(context, "_stickers");
        var managerType = manager.GetType();
        Form[] Forms() => ((System.Collections.IEnumerable)managerType.GetProperty("Forms", Private)!.GetValue(manager)!).Cast<Form>().ToArray();
        Form FormFor(Guid id) => Forms().Single(form => ((Bookmark)form.GetType().GetProperty("Bookmark")!.GetValue(form)!).Id == id);
        int expectedWidth = 0;
        try
        {
            Application.DoEvents();
            assert(Forms().Length == 0, "SM01 existing list mode does not create sticker windows");
            Pump(Call(manager, "SetEnabledAsync", true, false));
            assert(Forms().Length == 25 && Forms().All(form => form.Visible && !form.InvokeRequired), "SM02 sticker mode loads all active bookmarks beyond recent twenty on the UI thread");
            var first = FormFor(values[0].Id);
            first.Bounds = new Rectangle(100, 110, 380, 360);
            first.GetType().GetMethod("OnResizeEnd", Private)!.Invoke(first, [EventArgs.Empty]);
            Pump(Call(manager, "SavePendingAsync"));
            expectedWidth = (int)Math.Round(380 * 96F / first.DeviceDpi);
            PumpUntil(() => repository.GetStickerLayouts().Any(layout => layout.BookmarkId == values[0].Id && layout.Width == expectedWidth));
            var placement = repository.GetStickerLayouts().Single(layout => layout.BookmarkId == values[0].Id);
            assert(placement.Width == (int)Math.Round(380 * 96F / first.DeviceDpi) && placement.Height == (int)Math.Round(360 * 96F / first.DeviceDpi),
                "SM03 resized sticker commits expanded dimensions in logical pixels");
            var bounds = first.Bounds;
            repository.UpdateNote(values[0].Id, "복원 후에도 유지할 메모");
            var changed = repository.Get(values[0].Id)!;
            Invoke(context, "PublishBookmark", changed);
            assert(ReferenceEquals(first, FormFor(changed.Id)) && first.Bounds == bounds &&
                ((Bookmark)first.GetType().GetProperty("Bookmark")!.GetValue(first)!).Note == changed.Note,
                "SM04 bookmark updates reuse the same sticker and preserve placement");
            failDelete = true;
            Pump(Call(context, "DeleteAsync", changed));
            assert(repository.Get(changed.Id)!.DeletedAtUtc is null && Forms().Length == 25 && !first.IsDisposed,
                "SM05 rejected deletion preserves both database entry and sticker");
            failDelete = false;
            Pump(Call(context, "DeleteAsync", changed));
            assert(repository.Get(changed.Id)!.DeletedAtUtc is not null && Forms().Length == 24 && first.IsDisposed,
                "SM06 committed deletion removes only the matching sticker identity");
            Pump(Call(context, "DeleteAsync", values[1]));
            Pump(Call(context, "UndoAsync"));
            Pump(Call(context, "UndoAsync"));
            assert(Forms().Length == 25 && repository.Get(changed.Id)!.DeletedAtUtc is null && repository.Get(values[1].Id)!.DeletedAtUtc is null,
                "SM07 consecutive delete and undo restore both independent bookmarks");
            assert(FormFor(changed.Id).Bounds == bounds && repository.Get(changed.Id)!.Note == changed.Note,
                "SM08 undo restores previous sticker placement and note");
            var restoredForm = FormFor(changed.Id);
            restoredForm.Close();
            assert(!restoredForm.Visible && !restoredForm.IsDisposed && repository.Get(changed.Id)!.DeletedAtUtc is null,
                "SM09 native close hides without mutating shared bookmark storage");
            Pump(Call(manager, "SetEnabledAsync", false, false));
            assert(Forms().All(form => !form.Visible) && repository.ListActive().Count == 25,
                "SM10 switching to list hides all stickers without deleting records");
            Pump(Call(manager, "SetEnabledAsync", true, false));
            assert(Forms().All(form => form.Visible) && FormFor(changed.Id).Bounds == bounds,
                "SM11 switching back restores windows at their saved positions");
            Invoke(manager, "HideAll");
            var fresh = repository.UpsertCapture(new CapturedTarget(TargetKind.File, Path.Combine(directory, "추가 합성 문서.txt"))).Bookmark;
            Invoke(context, "PublishBookmark", fresh);
            assert(Forms().Length == 26 && Forms().All(form => !form.Visible), "SM12 capture respects explicit hide-all without losing new bookmark");
            Pump(Call(context, "DeleteAsync", fresh));
            typeof(BookmarkApplicationContext).GetField("_undoUntil", Private)!.SetValue(context, DateTimeOffset.UtcNow.AddSeconds(-1));
            Pump(Call(context, "UndoAsync"));
            assert(repository.Get(fresh.Id)!.DeletedAtUtc is not null, "SM13 expired quick undo cannot restore a record");
            Pump(Call(context, "RestoreBookmarkAsync", fresh.Id));
            assert(repository.Get(fresh.Id)!.DeletedAtUtc is null && repository.ListDeleted().Count == 0,
                "SM14 persistent deleted-items restoration works after quick undo expires");

            var clamp = managerType.GetMethod("RestoreBounds", BindingFlags.NonPublic | BindingFlags.Static)!;
            var offscreen = placement with { Left = 50000, Top = -50000, Width = 380, Height = 360 };
            var area = new Rectangle(-1920, 0, 1920, 1080);
            var corrected = (Rectangle)clamp.Invoke(null, [offscreen, area, 144, new Size(420, 450)])!;
            assert(area.Contains(corrected) && corrected.Size == new Size(570, 540),
                "SM15 missing/offscreen display placement clamps within work area at 150 percent scaling");
            context.ExitThread();
            assert(repository.ListActive().Count == 26 && Forms().Length == 0 && repository.GetStickerLayouts().Count == 26,
                "SM16 application shutdown preserves bookmarks and final sticker placements");
        }
        finally { context.ExitThread(); }

        using var reopened = new SqliteBookmarkRepository(Path.Combine(directory, "bookmarks.db"));
        assert(reopened.ListActive().Count == 26 && reopened.GetStickerLayouts().Single(layout => layout.BookmarkId == values[0].Id).Width == expectedWidth,
            "SM17 reopening database retains bookmarks and resized layout");
    }

    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static object? Invoke(object value, string name, params object[] arguments) => value.GetType().GetMethod(name, Private)!.Invoke(value, arguments);
    private static Task Call(object value, string name, params object[] arguments) => (Task)Invoke(value, name, arguments)!;
    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("Sticker integration operation did not finish.");
        task.GetAwaiter().GetResult();
    }
    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        if (!condition()) throw new TimeoutException("Sticker layout was not committed before the deadline.");
    }
}
