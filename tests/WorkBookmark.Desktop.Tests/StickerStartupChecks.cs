using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using WorkBookmark.App;
using WorkBookmark.Core;
using WorkBookmark.Storage;

namespace WorkBookmark.Desktop.Tests;

// Exercises the real application startup callback rather than explicitly enabling
// the manager, which would bypass the path used before the first view hotkey.
internal static class StickerStartupChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string directory, Action<bool, string> assert)
    {
        Directory.CreateDirectory(directory);
        var settings = UserSettings.Default with
        {
            CaptureHotkey = new Hotkey(7, (int)Keys.F23),
            RecentHotkey = new Hotkey(7, (int)Keys.F24),
            IntroShown = true,
            DisplayMode = BookmarkDisplayMode.Stickers
        };
        settings.Save(directory);
        using var repository = new SqliteBookmarkRepository(Path.Combine(directory, "bookmarks.db"));
        Bookmark Capture(string name) => repository.UpsertCapture(new CapturedTarget(TargetKind.File,
            Path.Combine(directory, name + ".txt"))).Bookmark;
        var first = Capture("시작 시 표시할 합성 책갈피");
        var collapsed = Capture("접힌 합성 책갈피");
        var deleted = Capture("삭제된 합성 책갈피");
        repository.SoftDelete(deleted.Id);
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        repository.SaveStickerLayout(new(first.Id, screen.DeviceName, 32, 42, 360, 340, false, false));
        repository.SaveStickerLayout(new(collapsed.Id, screen.DeviceName, 82, 92, 370, 350, true, false));

        using var worker = new WorkerClient();
        using var context = new BookmarkApplicationContext(repository, worker, directory);
        object manager = Field<object>(context, "_stickers");
        Form[] Forms() => ((System.Collections.IEnumerable)manager.GetType().GetProperty("Forms", Private)!.GetValue(manager)!).Cast<Form>().ToArray();
        Form FormFor(Guid id) => Forms().Single(form => BookmarkFor(form).Id == id);
        int resumeRequests = 0;
        Action<Bookmark> resumed = _ => resumeRequests++;
        manager.GetType().GetEvent("ResumeRequested", Private)!.GetAddMethod(true)!.Invoke(manager, [resumed]);

        try
        {
            // No call to ShowBookmarks/SetEnabledAsync: startup alone must reveal
            // active stickers while leaving a deliberately deleted record hidden.
            PumpUntil(() => Forms().Length == 2 && Forms().All(form => form.Visible));
            assert(Forms().All(form => !form.InvokeRequired) && Forms().All(form => BookmarkFor(form).Id != deleted.Id),
                "SS01 saved sticker mode automatically shows active bookmarks on the UI thread before any view hotkey");
            assert(Forms().All(IsNativeTopMost),
                "SS02 startup restores legacy unpinned stickers as native always-on-top windows");
            assert((bool)FormFor(collapsed.Id).GetType().GetProperty("IsCollapsed")!.GetValue(FormFor(collapsed.Id))! &&
                !Field<Form>(context, "_recent").Visible && UserSettings.Load(directory).DisplayMode == BookmarkDisplayMode.Stickers,
                "SS03 startup retains collapsed presentation and saved sticker mode without opening the list");
            var originalBounds = FormFor(first.Id).Bounds;
            var collapsedBounds = FormFor(collapsed.Id).Bounds;

            var added = Capture("실행 중 새로 저장한 합성 책갈피");
            Invoke(context, "PublishBookmark", added);
            assert(Forms().Length == 3 && FormFor(added.Id).Visible && IsNativeTopMost(FormFor(added.Id)),
                "SS04 a newly committed bookmark appears immediately above ordinary windows in sticker mode");

            Invoke(manager, "HideAll");
            var hiddenCapture = Capture("잠시 숨긴 동안 저장한 합성 책갈피");
            Invoke(context, "PublishBookmark", hiddenCapture);
            repository.UpdateNote(first.Id, "숨긴 동안 갱신한 메모");
            Invoke(context, "PublishBookmark", repository.Get(first.Id)!);
            assert(Forms().Length == 4 && Forms().All(form => !form.Visible) && repository.ListActive().Count == 4,
                "SS05 explicit hide-all remains effective for existing updates and new captures without deleting bookmarks");

            context.ShowBookmarks();
            PumpUntil(() => Forms().Length == 4 && Forms().All(form => form.Visible));
            assert(Forms().All(IsNativeTopMost) && FormFor(first.Id).Bounds == originalBounds &&
                FormFor(collapsed.Id).Bounds == collapsedBounds && BookmarkFor(FormFor(first.Id)).Note == "숨긴 동안 갱신한 메모",
                "SS06 explicit reveal restores every sticker above ordinary windows with its position and collapsed size intact");
            assert(resumeRequests == 0 && !worker.IsBusy && repository.ListActive().All(bookmark => bookmark.LastResumeAtUtc is null),
                "SS07 startup, capture and reveal never request resume or open a bookmarked target");
        }
        finally { context.ExitThread(); }
    }

    private static Bookmark BookmarkFor(Form form) => (Bookmark)form.GetType().GetProperty("Bookmark")!.GetValue(form)!;
    private static bool IsNativeTopMost(Form form) => form.TopMost && (GetWindowLong(form.Handle, -20) & 0x00000008) != 0;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static object? Invoke(object value, string name, params object[] arguments) => value.GetType().GetMethod(name, Private)!.Invoke(value, arguments);
    private static void PumpUntil(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        if (!condition()) throw new TimeoutException("Sticker startup or reveal did not show the saved bookmarks.");
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);
}
