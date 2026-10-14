using System.Diagnostics;
using System.Reflection;
using WorkBookmark.App;
using WorkBookmark.Core;
using WorkBookmark.Storage;

namespace WorkBookmark.Desktop.Tests;

internal static class FolderIntegrationChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string directory, Action<bool, string> assert)
    {
        Directory.CreateDirectory(directory);
        (UserSettings.Default with
        {
            CaptureHotkey = new Hotkey(7, (int)Keys.F15), RecentHotkey = new Hotkey(7, (int)Keys.F16),
            IntroShown = true, DisplayMode = BookmarkDisplayMode.List,
            StickerPresentationVersion = UserSettings.CurrentStickerPresentationVersion
        }).Save(directory);
        bool fail = false;
        using var repository = new SqliteBookmarkRepository(Path.Combine(directory, "bookmarks.db"), operation =>
        {
            if (fail && operation != "sticker_layout") throw new IOException("Injected folder write failure");
        });
        var a = repository.UpsertCapture(new CapturedTarget(TargetKind.File, Path.Combine(directory, "기획.txt"))).Bookmark;
        var b = repository.UpsertCapture(new CapturedTarget(TargetKind.WebPage, "https://example.invalid/research", PageTitle: "시장 조사")).Bookmark;
        var c = repository.UpsertCapture(new CapturedTarget(TargetKind.File, Path.Combine(directory, "예산.xlsx"))).Bookmark;
        repository.SetColor(b.Id, BookmarkColor.Blue);
        b = repository.Get(b.Id)!;
        using var worker = new WorkerClient();
        using var context = new BookmarkApplicationContext(repository, worker, directory);
        Application.DoEvents();
        object manager = Field<object>(context, "_stickers");
        var recent = Field<Form>(context, "_recent");
        Pump((Task)Invoke(manager, "SetEnabledAsync", true, false)!);
        Pump((Task)recent.GetType().GetMethod("OpenAsync")!.Invoke(recent, null)!);
        var list = Field<ListBox>(recent, "_items");

        Pump(Change(b, a.Id));
        var folder = repository.ListActive().Single(item => item.IsFolder);
        assert(folder.ParentId is null && folder.Target.Path == "" && !folder.FolderExpanded &&
            repository.Get(a.Id) == a with { ParentId = folder.Id } && repository.Get(b.Id) == b with { ParentId = folder.Id },
            "FI01 bookmark-on-bookmark creates one closed linkless folder and preserves both original bookmarks");
        assert(Listed(folder.Id) is not null && Listed(a.Id) is null && Listed(b.Id) is null &&
            Sticker(folder.Id).Visible && !Sticker(a.Id).Visible && !Sticker(b.Id).Visible && Sticker(c.Id).Visible,
            "FI02 atomic grouping reaches both views without exposing children of a closed folder");

        Field<Action<Bookmark>>(recent, "FolderToggleRequested")(folder);
        PumpUntil(() => repository.Get(folder.Id)!.FolderExpanded && Listed(a.Id) is not null && Sticker(a.Id).Visible);
        assert(Listed(b.Id)!.Color == BookmarkColor.Blue && Current(Sticker(b.Id)).Color == BookmarkColor.Blue,
            "FI03 list folder opening reveals preserved child colors in both views");
        var bounds = Sticker(folder.Id).Bounds;
        Pump((Task)Invoke(context, "RenameFolderAsync", repository.Get(folder.Id)!, "신규 서비스")!);
        assert(Listed(folder.Id)!.DisplayName == "신규 서비스" && Current(Sticker(folder.Id)).DisplayName == "신규 서비스" && Sticker(folder.Id).Bounds == bounds,
            "FI04 folder rename updates both views without moving the folder");

        var create = (Task<Bookmark>)Invoke(context, "CreateFolderAsync", "자료", (Guid?)null)!;
        Pump(create);
        var nested = create.Result;
        Pump(Change(b, nested.Id));
        Pump(Change(nested, folder.Id));
        assert(repository.ListActive().Count(item => item.IsFolder) == 2 && repository.Get(nested.Id)!.ParentId == folder.Id &&
            repository.Get(b.Id)!.ParentId == nested.Id && Sticker(nested.Id).Visible && !Sticker(b.Id).Visible && Listed(b.Id) is null,
            "FI05 folders and bookmarks move into folders without creating extra wrappers; nested closed state is retained");
        Field<Action<Bookmark>>(Sticker(nested.Id), "FolderToggleRequested")(repository.Get(nested.Id)!);
        PumpUntil(() => repository.Get(nested.Id)!.FolderExpanded && Sticker(b.Id).Visible && Listed(b.Id) is not null);
        assert(Current(Sticker(nested.Id)).FolderExpanded && Listed(nested.Id)!.FolderExpanded,
            "FI06 sticker folder toggle commits the same open state to the list");
        Pump(Change(repository.Get(folder.Id)!, b.Id));
        assert(repository.Get(folder.Id)!.ParentId is null && repository.ListActive().Count(item => item.IsFolder) == 2,
            "FI07 dropping a folder onto its own descendant refuses a cycle without creating a wrapper");

        var allBefore = repository.ListActive().ToArray();
        fail = true;
        Pump(Change(c, a.Id));
        fail = false;
        assert(repository.ListActive().SequenceEqual(allBefore) && Current(Sticker(c.Id)).ParentId is null && Listed(c.Id)!.ParentId is null,
            "FI08 failed ordinary-target grouping rolls back the new folder and all parent changes in both views");

        Pump((Task)Invoke(context, "ResumeAsync", repository.Get(folder.Id)!, (TargetSnapshot?)null)!);
        assert(!repository.Get(folder.Id)!.FolderExpanded && repository.Get(folder.Id)!.LastResumeAtUtc is null &&
            !Field<bool>(context, "_operationInFlight") && !Sticker(a.Id).Visible && !Sticker(nested.Id).Visible && !Sticker(b.Id).Visible,
            "FI09 folder activation closes its entire subtree without sending an external resume request or recording a resume");
        fail = true;
        Pump((Task)Invoke(context, "ToggleFolderAsync", repository.Get(folder.Id)!)!);
        fail = false;
        assert(!repository.Get(folder.Id)!.FolderExpanded && !Current(Sticker(folder.Id)).FolderExpanded && !Listed(folder.Id)!.FolderExpanded,
            "FI10 failed folder-open persistence leaves the folder closed everywhere");

        Invoke(manager, "HideAll");
        Pump((Task)Invoke(context, "ChangeColorAsync", repository.Get(folder.Id)!, BookmarkColor.Purple)!);
        assert(repository.Get(folder.Id)!.Color == BookmarkColor.Purple && Forms().All(form => !form.Visible),
            "FI11 folder colors remain independent and never defeat hide-all");
        Pump((Task)Invoke(manager, "ShowAllAsync", false)!);
        assert(Sticker(folder.Id).Visible && !Sticker(a.Id).Visible && !Sticker(nested.Id).Visible,
            "FI12 reload preserves saved folder closure while showing roots");
        Pump((Task)Invoke(context, "DeleteAsync", repository.Get(folder.Id)!)!);
        assert(Listed(a.Id) is not null && Listed(nested.Id) is not null && Sticker(a.Id).Visible,
            "FI13 deleting a folder preserves its children as accessible roots");
        Pump((Task)Invoke(context, "RestoreBookmarkAsync", folder.Id)!);
        assert(repository.Get(folder.Id)!.Color == BookmarkColor.Purple && !Sticker(a.Id).Visible && Listed(a.Id) is null && repository.Get(a.Id)!.ParentId == folder.Id,
            "FI14 restoring the closed folder restores membership, color and child visibility without losing targets");

        Task Change(Bookmark item, Guid? target) => (Task)Invoke(context, "ChangeParentAsync", item, target)!;
        Bookmark? Listed(Guid id) => list.Items.Cast<Bookmark>().FirstOrDefault(item => item.Id == id);
        Form[] Forms() => ((System.Collections.IEnumerable)manager.GetType().GetProperty("Forms", Private)!.GetValue(manager)!).Cast<Form>().ToArray();
        Form Sticker(Guid id) => Forms().Single(form => Current(form).Id == id);
    }

    private static Bookmark Current(Form form) => (Bookmark)form.GetType().GetProperty("Bookmark")!.GetValue(form)!;
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static object? Invoke(object owner, string name, params object?[] args) => owner.GetType().GetMethod(name, Private)!.Invoke(owner, args);
    private static void Pump(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(15)) { Application.DoEvents(); Thread.Sleep(1); }
        if (!done()) throw new TimeoutException("Folder integration did not complete.");
    }
}
