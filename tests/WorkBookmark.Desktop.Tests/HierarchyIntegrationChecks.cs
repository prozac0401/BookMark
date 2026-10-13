using System.Diagnostics;
using System.Reflection;
using WorkBookmark.App;
using WorkBookmark.Core;
using WorkBookmark.Storage;

namespace WorkBookmark.Desktop.Tests;

internal static class HierarchyIntegrationChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string directory, Action<bool, string> assert)
    {
        Directory.CreateDirectory(directory);
        (UserSettings.Default with
        {
            CaptureHotkey = new Hotkey(7, (int)Keys.F15),
            RecentHotkey = new Hotkey(7, (int)Keys.F16),
            IntroShown = true,
            DisplayMode = BookmarkDisplayMode.List,
            StickerPresentationVersion = UserSettings.CurrentStickerPresentationVersion
        }).Save(directory);
        bool failWrites = false;
        using var repository = new SqliteBookmarkRepository(Path.Combine(directory, "bookmarks.db"), _ =>
        {
            if (failWrites) throw new IOException("Injected hierarchy write failure");
        });
        var rows = Enumerable.Range(0, 26).Select(index => repository.UpsertCapture(
            new CapturedTarget(TargetKind.File, Path.Combine(directory, $"계층 {index:00}.txt"))).Bookmark).ToArray();
        var parent = rows[0]; var child = rows[25]; var grandchild = rows[24];
        using var worker = new WorkerClient();
        using var context = new BookmarkApplicationContext(repository, worker, directory);
        Application.DoEvents();
        object manager = Field<object>(context, "_stickers");
        var recent = Field<Form>(context, "_recent");
        Pump((Task)Invoke(manager, "SetEnabledAsync", true, false)!);
        Pump((Task)recent.GetType().GetMethod("OpenAsync")!.Invoke(recent, null)!);
        var list = Field<ListBox>(recent, "_items");
        assert(list.Items.Count == 26 && list.Items.Cast<Bookmark>().Any(item => item.Id == parent.Id),
            "HI01 production list loader includes old parents beyond the recent twenty");

        var listRequest = Field<Action<Bookmark, Guid?>>(recent, "ParentChangeRequested");
        listRequest(child, parent.Id);
        PumpUntil(() => repository.Get(child.Id)!.ParentId == parent.Id && Listed(child.Id).ParentId == parent.Id && Sticker(child.Id).ParentId == parent.Id);
        assert(list.Items.Cast<Bookmark>().ToList().FindIndex(item => item.Id == child.Id) ==
            list.Items.Cast<Bookmark>().ToList().FindIndex(item => item.Id == parent.Id) + 1,
            "HI02 list drop commits the relationship and both views show the child below its old parent");

        var stickerRequest = Field<Action<Bookmark, Guid?>>(manager, "ParentChangeRequested");
        stickerRequest(grandchild, child.Id);
        PumpUntil(() => repository.Get(grandchild.Id)!.ParentId == child.Id && Listed(grandchild.Id).ParentId == child.Id && Sticker(grandchild.Id).ParentId == child.Id);
        assert(repository.Get(parent.Id)!.ParentId is null && repository.Get(child.Id)!.ParentId == parent.Id,
            "HI03 sticker drop updates the shared graph and preserves multiple levels");

        Pump(Change(parent, grandchild.Id));
        assert(repository.Get(parent.Id)!.ParentId is null && Listed(parent.Id).ParentId is null && Sticker(parent.Id).ParentId is null,
            "HI04 rejected cycle leaves both presentations and storage unchanged");

        failWrites = true;
        Pump(Change(repository.Get(child.Id)!, null));
        failWrites = false;
        assert(repository.Get(child.Id)!.ParentId == parent.Id && Listed(child.Id).ParentId == parent.Id && Sticker(child.Id).ParentId == parent.Id,
            "HI05 failed hierarchy commit preserves the committed relationship in both views");
        assert(Field<Label>(recent, "_status").Text.Contains("저장하지 못했습니다", StringComparison.Ordinal),
            "HI06 failed drop leaves an actionable status in the list");

        Invoke(manager, "HideAll");
        Pump(Change(repository.Get(child.Id)!, null));
        assert(repository.Get(child.Id)!.ParentId is null && Listed(child.Id).ParentId is null && Sticker(child.Id).ParentId is null && Forms().All(form => !form.Visible),
            "HI07 detach persists without revealing hidden stickers");
        assert(repository.Get(grandchild.Id)!.ParentId == child.Id,
            "HI08 moving a branch to the root preserves its descendants");

        Pump((Task)Invoke(context, "DeleteAsync", repository.Get(child.Id)!)!);
        assert(Forms().All(form => BookmarkOf(form).Id != child.Id) && Listed(grandchild.Id).Id == grandchild.Id,
            "HI09 deleting a parent leaves the child reachable in the list and stickers");
        Pump((Task)Invoke(context, "RestoreBookmarkAsync", child.Id)!);
        assert(repository.Get(grandchild.Id)!.ParentId == child.Id && Listed(grandchild.Id).ParentId == child.Id && Sticker(grandchild.Id).ParentId == child.Id,
            "HI10 restoring a parent restores its shared hierarchy without losing descendants");

        Task Change(Bookmark item, Guid? parentId) => (Task)Invoke(context, "ChangeParentAsync", item, parentId)!;
        Bookmark Listed(Guid id) => list.Items.Cast<Bookmark>().Single(item => item.Id == id);
        Form[] Forms() => ((System.Collections.IEnumerable)manager.GetType().GetProperty("Forms", Private)!.GetValue(manager)!).Cast<Form>().ToArray();
        Bookmark Sticker(Guid id) => Forms().Select(BookmarkOf).Single(item => item.Id == id);
    }

    private static Bookmark BookmarkOf(Form form) => (Bookmark)form.GetType().GetProperty("Bookmark")!.GetValue(form)!;
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static object? Invoke(object owner, string name, params object?[] args) => owner.GetType().GetMethod(name, Private)!.Invoke(owner, args);
    private static void Pump(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> complete)
    {
        var elapsed = Stopwatch.StartNew();
        while (!complete() && elapsed.Elapsed < TimeSpan.FromSeconds(15)) { Application.DoEvents(); Thread.Sleep(1); }
        if (!complete()) throw new TimeoutException("Hierarchy integration did not complete.");
    }
}
