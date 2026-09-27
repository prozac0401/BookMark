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
            DisplayMode = BookmarkDisplayMode.List
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
        repository.SaveStickerLayout(new(deleted.Id, screen.DeviceName, 122, 132, 390, 380, true, false));

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
                "SS01 a legacy List installation automatically starts active stickers on the UI thread before any view hotkey");
            assert(Forms().All(IsNativeTopMost),
                "SS02 startup restores legacy unpinned stickers as native always-on-top windows");
            assert((bool)FormFor(collapsed.Id).GetType().GetProperty("IsCollapsed")!.GetValue(FormFor(collapsed.Id))! &&
                !Field<Form>(context, "_recent").Visible && UserSettings.Load(directory).DisplayMode == BookmarkDisplayMode.Stickers,
                "SS03 startup retains collapsed presentation and saved sticker mode without opening the list");
            var size = (Size)manager.GetType().GetProperty("DefaultExpandedSize96", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var layouts = repository.GetStickerLayouts().ToDictionary(value => value.BookmarkId);
            assert(layouts.Values.All(layout => layout.Width == size.Width && layout.Height == size.Height) &&
                layouts[first.Id].Left == 32 && layouts[first.Id].Top == 42 &&
                layouts[collapsed.Id].Left == 82 && layouts[collapsed.Id].Top == 92 && layouts[collapsed.Id].IsCollapsed &&
                layouts[deleted.Id].Left == 122 && layouts[deleted.Id].Top == 132 && layouts[deleted.Id].IsCollapsed &&
                repository.Get(deleted.Id)!.DeletedAtUtc is not null &&
                UserSettings.Load(directory).StickerPresentationVersion == UserSettings.CurrentStickerPresentationVersion,
                "SS08 startup resizes active, collapsed and deleted layouts before recording completion while retaining their placement");
            assert(FormFor(first.Id).Size == (Size)FormFor(first.Id).GetType().GetProperty("DefaultExpandedSize")!.GetValue(FormFor(first.Id))!,
                "SS09 the migrated outer size matches the default client size including the DPI-scaled window frame");
            var originalBounds = FormFor(first.Id).Bounds;
            var collapsedBounds = FormFor(collapsed.Id).Bounds;

            var added = Capture("실행 중 새로 저장한 합성 책갈피");
            Invoke(context, "PublishBookmark", added);
            assert(Forms().Length == 3 && FormFor(added.Id).Visible && IsNativeTopMost(FormFor(added.Id)),
                "SS04 a newly committed bookmark appears immediately above ordinary windows in sticker mode");
            assert(FormFor(added.Id).Size == (Size)FormFor(added.Id).GetType().GetProperty("DefaultExpandedSize")!.GetValue(FormFor(added.Id))!,
                "SS10 newly captured bookmarks use the same compact default outer size");

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

        var customLayout = repository.GetStickerLayouts().Single(layout => layout.BookmarkId == first.Id)
            with { Width = 410, Height = 330 };
        repository.SaveStickerLayout(customLayout);
        var chosen = UserSettings.Load(directory) with { DisplayMode = BookmarkDisplayMode.List };
        chosen.Save(directory);
        using (var nextWorker = new WorkerClient())
        using (var next = new BookmarkApplicationContext(repository, nextWorker, directory))
        {
            try
            {
                Application.DoEvents();
                object nextManager = Field<object>(next, "_stickers");
                assert(!((System.Collections.IEnumerable)nextManager.GetType().GetProperty("Forms", Private)!.GetValue(nextManager)!).Cast<Form>().Any() &&
                    UserSettings.Load(directory).DisplayMode == BookmarkDisplayMode.List &&
                    repository.GetStickerLayouts().Single(layout => layout.BookmarkId == first.Id) == customLayout,
                    "SS11 the next startup retains the user's later List choice and custom size without repeating migration");
            }
            finally { next.ExitThread(); }
        }
        (chosen with { DisplayMode = BookmarkDisplayMode.Stickers }).Save(directory);
        using (var nextWorker = new WorkerClient())
        using (var next = new BookmarkApplicationContext(repository, nextWorker, directory))
        {
            try
            {
                object nextManager = Field<object>(next, "_stickers");
                Form[] Restarted() => ((System.Collections.IEnumerable)nextManager.GetType().GetProperty("Forms", Private)!.GetValue(nextManager)!).Cast<Form>().ToArray();
                PumpUntil(() => Restarted().Length == repository.ListActive().Count && Restarted().All(form => form.Visible));
                var restored = Restarted().Single(form => BookmarkFor(form).Id == first.Id);
                var expected = (Rectangle)nextManager.GetType().GetMethod("RestoreBounds", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [customLayout, screen.WorkingArea, restored.DeviceDpi, restored.MinimumSize])!;
                assert(restored.Bounds == expected,
                    "SS12 later user-sized stickers restore at restart without returning to the compact default");
            }
            finally { next.ExitThread(); }
        }
        VersionOneCompactStartup(Path.Combine(directory, "version-one"), assert);
        EmptyAndInvalidStartup(Path.Combine(directory, "fresh"), assert);
        FailedUpgradeStartup(Path.Combine(directory, "failed-upgrade"), assert);
    }

    private static void VersionOneCompactStartup(string directory, Action<bool, string> assert)
    {
        Directory.CreateDirectory(directory);
        var settings = UserSettings.Default with
        {
            CaptureHotkey = new Hotkey(7, (int)Keys.F23), RecentHotkey = new Hotkey(7, (int)Keys.F24),
            IntroShown = true, DisplayMode = BookmarkDisplayMode.List, StickerPresentationVersion = 1
        };
        settings.Save(directory);
        using var repository = new SqliteBookmarkRepository(Path.Combine(directory, "bookmarks.db"));
        Bookmark Capture(string name) => repository.UpsertCapture(new CapturedTarget(TargetKind.File, Path.Combine(directory, name + ".txt"))).Bookmark;
        var first = Capture("old-default");
        var custom = Capture("custom-size");
        var deleted = Capture("deleted-default");
        repository.SoftDelete(deleted.Id);
        Type managerType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.StickerManager")!;
        var legacySize = (Size)managerType.GetProperty("LegacyDefaultExpandedSize96", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var compactSize = (Size)managerType.GetProperty("DefaultExpandedSize96", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var customLayout = new StickerLayout(custom.Id, screen.DeviceName, 72, 82, legacySize.Width + 100, legacySize.Height + 70, AlwaysOnTop: true);
        repository.SaveStickerLayout(new(first.Id, screen.DeviceName, 32, 42, legacySize.Width, legacySize.Height));
        repository.SaveStickerLayout(customLayout);
        repository.SaveStickerLayout(new(deleted.Id, screen.DeviceName, 112, 122, legacySize.Width, legacySize.Height, true));
        using (var worker = new WorkerClient())
        using (var context = new BookmarkApplicationContext(repository, worker, directory))
        {
            try
            {
                PumpUntil(() => Field<UserSettings>(context, "_settings").StickerPresentationVersion == UserSettings.CurrentStickerPresentationVersion);
                var manager = Field<object>(context, "_stickers");
                var layouts = repository.GetStickerLayouts().ToDictionary(layout => layout.BookmarkId);
                assert(UserSettings.Load(directory).DisplayMode == BookmarkDisplayMode.List &&
                    !((System.Collections.IEnumerable)managerType.GetProperty("Forms", Private)!.GetValue(manager)!).Cast<Form>().Any() &&
                    layouts[first.Id].Width == compactSize.Width && layouts[first.Id].Height == compactSize.Height &&
                    layouts[custom.Id] == customLayout && layouts[deleted.Id].Width == compactSize.Width &&
                    layouts[deleted.Id].Height == compactSize.Height && layouts[deleted.Id].IsCollapsed && repository.Get(deleted.Id)!.DeletedAtUtc is not null,
                    "SS17 version-one startup compacts only old defaults, keeps custom outer bounds and the List choice, and leaves deleted bookmarks deleted");
            }
            finally { context.ExitThread(); }
        }
        (UserSettings.Load(directory) with { DisplayMode = BookmarkDisplayMode.Stickers }).Save(directory);
        using (var worker = new WorkerClient())
        using (var context = new BookmarkApplicationContext(repository, worker, directory))
        {
            try
            {
                var manager = Field<object>(context, "_stickers");
                Form[] Forms() => ((System.Collections.IEnumerable)managerType.GetProperty("Forms", Private)!.GetValue(manager)!).Cast<Form>().ToArray();
                PumpUntil(() => Forms().Length == 2 && Forms().All(form => form.Visible));
                var compact = Forms().Single(form => BookmarkFor(form).Id == first.Id);
                var restored = Forms().Single(form => BookmarkFor(form).Id == custom.Id);
                var expected = (Rectangle)managerType.GetMethod("RestoreBounds", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [customLayout, screen.WorkingArea, restored.DeviceDpi, restored.MinimumSize])!;
                assert(compact.ClientSize == new Size((int)Math.Round(260 * compact.DeviceDpi / 96F), (int)Math.Round(88 * compact.DeviceDpi / 96F)) &&
                    restored.Bounds == expected && repository.GetStickerLayouts().Single(layout => layout.BookmarkId == custom.Id) == customLayout,
                    "SS18 reopening version-two stickers restores the compact default and the user's unchanged larger window");
            }
            finally { context.ExitThread(); }
        }
    }

    private static void EmptyAndInvalidStartup(string directory, Action<bool, string> assert)
    {
        Directory.CreateDirectory(directory);
        using var repository = new SqliteBookmarkRepository(Path.Combine(directory, "bookmarks.db"));
        using (var worker = new WorkerClient())
        using (var context = new BookmarkApplicationContext(repository, worker, directory))
        {
            try
            {
                PumpUntil(() => Field<UserSettings>(context, "_settings").StickerPresentationVersion == UserSettings.CurrentStickerPresentationVersion &&
                    Field<UserSettings>(context, "_settings").IntroShown);
                object manager = Field<object>(context, "_stickers");
                assert(UserSettings.Load(directory).DisplayMode == BookmarkDisplayMode.Stickers &&
                    !((System.Collections.IEnumerable)manager.GetType().GetProperty("Forms", Private)!.GetValue(manager)!).Cast<Form>().Any() &&
                    !Field<Form>(context, "_recent").Visible && !worker.IsBusy,
                    "SS13 a new empty installation defaults to stickers without inventing a window or opening a target");
            }
            finally { context.ExitThread(); }
        }
        string path = Path.Combine(directory, "settings.json"), invalid = "{invalid synthetic settings";
        File.WriteAllText(path, invalid);
        using (var worker = new WorkerClient())
        using (var context = new BookmarkApplicationContext(repository, worker, directory))
        {
            try
            {
                Application.DoEvents();
                assert(File.ReadAllText(path) == invalid && Field<bool>(context, "_settingsInvalid"),
                    "SS14 startup does not overwrite corrupt settings with a presentation completion marker");
            }
            finally { context.ExitThread(); }
        }
    }

    private static void FailedUpgradeStartup(string directory, Action<bool, string> assert)
    {
        Directory.CreateDirectory(directory);
        var settings = UserSettings.Default with
        {
            CaptureHotkey = new Hotkey(7, (int)Keys.F23), RecentHotkey = new Hotkey(7, (int)Keys.F24), IntroShown = true
        };
        settings.Save(directory);
        using var stored = new SqliteBookmarkRepository(Path.Combine(directory, "bookmarks.db"));
        var bookmark = stored.UpsertCapture(new CapturedTarget(TargetKind.File, Path.Combine(directory, "synthetic.txt"))).Bookmark;
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        stored.SaveStickerLayout(new(bookmark.Id, screen.DeviceName, 32, 42, 440, 400));
        using var repository = new FailFirstLayoutReadRepository(stored);
        using (var worker = new WorkerClient())
        using (var context = new BookmarkApplicationContext(repository, worker, directory))
        {
            try
            {
                object manager = Field<object>(context, "_stickers");
                Form[] Forms() => ((System.Collections.IEnumerable)manager.GetType().GetProperty("Forms", Private)!.GetValue(manager)!).Cast<Form>().ToArray();
                PumpUntil(() => Forms().Length == 1 && Forms()[0].Visible);
                assert(UserSettings.Load(directory).StickerPresentationVersion == 0 && !worker.IsBusy,
                    "SS15 a failed startup upgrade leaves its marker incomplete while the previously selected sticker mode remains usable");
            }
            finally { context.ExitThread(); }
        }
        using (var worker = new WorkerClient())
        using (var context = new BookmarkApplicationContext(repository, worker, directory))
        {
            try
            {
                PumpUntil(() => UserSettings.Load(directory).StickerPresentationVersion == UserSettings.CurrentStickerPresentationVersion);
                assert(UserSettings.Load(directory).DisplayMode == BookmarkDisplayMode.Stickers,
                    "SS16 the next startup retries and completes a previously failed presentation upgrade");
            }
            finally { context.ExitThread(); }
        }
    }

    private sealed class FailFirstLayoutReadRepository(IBookmarkRepository inner) : IBookmarkRepository
    {
        private int _reads;
        public IReadOnlyList<StickerLayout> GetStickerLayouts() => Interlocked.Increment(ref _reads) == 1
            ? throw new IOException("Injected startup layout read failure") : inner.GetStickerLayouts();
        public void SaveStickerLayout(StickerLayout layout) => inner.SaveStickerLayout(layout);
        public CaptureCommit UpsertCapture(CapturedTarget target) => inner.UpsertCapture(target);
        public SearchResults List(string query = "") => inner.List(query);
        public IReadOnlyList<Bookmark> ListActive() => inner.ListActive();
        public Bookmark? Get(Guid id) => inner.Get(id);
        public void UpdateNote(Guid id, string note) => inner.UpdateNote(id, note);
        public void SoftDelete(Guid id) => inner.SoftDelete(id);
        public void Restore(Guid id) => inner.Restore(id);
        public void RecordResume(Guid id, ResultCode result) => inner.RecordResume(id, result);
        public void Relink(Guid id, CapturedTarget target) => inner.Relink(id, target);
        public void Dispose() { }
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
