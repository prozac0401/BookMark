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
            if (fail && operation != "sticker-layout") throw new IOException("Injected folder write failure");
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

        Pump((Task)recent.GetType().GetMethod("OpenAsync")!.Invoke(recent, null)!);
        var search = Field<TextBox>(recent, "_search");
        int recentDeactivations = 0;
        recent.Deactivate += (_, _) => recentDeactivations++;
        for (int iteration = 1; iteration <= 3; iteration++)
        {
            Pump((Task)Invoke(context, "ToggleFolderAsync", repository.Get(folder.Id)!)!);
            assert(!repository.Get(folder.Id)!.FolderExpanded && Listed(a.Id) is null && Listed(b.Id) is null &&
                !Sticker(a.Id).Visible && !Sticker(b.Id).Visible && recent.Visible && search.Focused && recentDeactivations == 0,
                $"FI28 repetition {iteration}: committed folder closure hides child stickers while retaining the list and search focus");
            Pump((Task)Invoke(context, "ToggleFolderAsync", repository.Get(folder.Id)!)!);
            assert(repository.Get(folder.Id)!.FolderExpanded && Listed(a.Id) is not null && Listed(b.Id) is not null &&
                Sticker(a.Id).Visible && Sticker(b.Id).Visible && recent.Visible && search.Focused && recentDeactivations == 0,
                $"FI29 repetition {iteration}: committed folder opening reveals child stickers without deactivating the list");
        }
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

        // The earlier checks deliberately exercise both views while configured for list mode.
        // Switch this fixture's routing to sticker mode without writing system startup settings.
        typeof(BookmarkApplicationContext).GetField("_settings", Private)!.SetValue(context,
            Field<UserSettings>(context, "_settings") with { DisplayMode = BookmarkDisplayMode.Stickers });
        repository.UpdateNote(folder.Id, "이름 편집으로 바뀌면 안 되는 폴더 메모");
        Pump((Task)Invoke(context, "RefreshBookmarkAsync", folder.Id)!);
        var folderBeforeRename = repository.Get(folder.Id)!;
        var folderSticker = Sticker(folder.Id);
        var folderWindow = folderSticker.Handle;
        var folderBounds = folderSticker.Bounds;

        // Start at the real sticker event so manager forwarding and application routing
        // must provide the inline editor's persistence callback.
        Field<Action<Bookmark>>(folderSticker, "RenameFolderRequested")(folderBeforeRename);
        PumpUntil(() => Editing(folderSticker) && !Field<bool>(context, "_openingNote"));
        var nameEditor = Field<TextBox>(folderSticker, "_noteEditor");
        assert(nameEditor.Text == folderBeforeRename.DisplayName && nameEditor.MaxLength == 100 && nameEditor.Visible &&
            folderSticker.Handle == folderWindow && NameDialog(folder.Id) is not { IsDisposed: false } &&
            !Sticker(a.Id).Visible && !Sticker(nested.Id).Visible,
            "FI15 the sticker rename event opens its own 100-character inline name editor without a separate window or revealing closed-folder children");
        nameEditor.Text = "스티커에서 변경한 폴더";
        Pump((Task)Invoke(folderSticker, "SaveNoteAsync")!);
        var renamedFolder = folderBeforeRename with { DisplayName = "스티커에서 변경한 폴더" };
        assert(repository.Get(folder.Id) == renamedFolder && Current(folderSticker) == renamedFolder && Listed(folder.Id) == renamedFolder &&
            !Editing(folderSticker) && folderSticker.Handle == folderWindow && folderSticker.Bounds == folderBounds,
            "FI16 inline folder-name save commits to SQLite and both views while preserving note, history, color, closure and normal geometry");

        Field<Action<Bookmark>>(folderSticker, "RenameFolderRequested")(renamedFolder);
        PumpUntil(() => Editing(folderSticker) && !Field<bool>(context, "_openingNote"));
        nameEditor.Text = "실패 후 재시도할 이름";
        fail = true;
        try { Pump((Task)Invoke(folderSticker, "SaveNoteAsync")!); }
        finally { fail = false; }
        assert(Editing(folderSticker) && !nameEditor.ReadOnly && nameEditor.Text == "실패 후 재시도할 이름" &&
            repository.Get(folder.Id) == renamedFolder && Current(folderSticker) == renamedFolder && Listed(folder.Id) == renamedFolder &&
            Field<Label>(folderSticker, "_noteStatus").Text.Contains("저장", StringComparison.Ordinal),
            "FI17 failed inline folder rename retains the editable draft and reports failure without publishing uncommitted names to either view");
        Pump((Task)Invoke(folderSticker, "SaveNoteAsync")!);
        var retriedFolder = renamedFolder with { DisplayName = "실패 후 재시도할 이름" };
        assert(repository.Get(folder.Id) == retriedFolder && Current(folderSticker) == retriedFolder && Listed(folder.Id) == retriedFolder &&
            !Editing(folderSticker) && folderSticker.Bounds == folderBounds,
            "FI18 retry commits the retained folder-name draft through the original application callback and restores normal placement");

        Field<Action<Bookmark>>(folderSticker, "RenameFolderRequested")(retriedFolder);
        PumpUntil(() => Editing(folderSticker) && !Field<bool>(context, "_openingNote"));
        nameEditor.Text = "취소할 이름";
        Field<Button>(folderSticker, "_cancelNote").PerformClick();
        Application.DoEvents();
        assert(!Editing(folderSticker) && repository.Get(folder.Id) == retriedFolder && Current(folderSticker) == retriedFolder &&
            Listed(folder.Id) == retriedFolder && folderSticker.Bounds == folderBounds && !Sticker(nested.Id).Visible,
            "FI19 cancelling inline folder rename discards only the draft and preserves both committed views and closed-folder visibility");

        // A rename request may target an existing sticker hidden under a closed ancestor.
        // It must use the normal name dialog rather than bypass the ancestor's visibility.
        var nestedBeforeRename = repository.Get(nested.Id)!;
        Field<Action<Bookmark>>(manager, "RenameFolderRequested")(nestedBeforeRename);
        PumpUntil(() => NameDialog(nested.Id) is { IsDisposed: false, Visible: true } && !Field<bool>(context, "_openingNote"));
        var fallback = NameDialog(nested.Id)!;
        assert(Field<TextBox>(fallback, "_name").Text == nestedBeforeRename.DisplayName && !Editing(Sticker(nested.Id)) &&
            !Sticker(nested.Id).Visible && !Sticker(b.Id).Visible && !repository.Get(folder.Id)!.FolderExpanded,
            "FI20 renaming a folder hidden beneath a closed ancestor falls back to the name dialog without revealing its sticker or descendants");
        Field<TextBox>(fallback, "_name").Text = "숨겨진 폴더 취소";
        Field<Button>(fallback, "_cancel").PerformClick();
        assert(fallback.IsDisposed && repository.Get(nested.Id) == nestedBeforeRename && !Sticker(nested.Id).Visible,
            "FI21 cancelling the hidden-folder fallback leaves its name, expansion and membership unchanged");

        Field<Action<Bookmark>>(manager, "RenameFolderRequested")(nestedBeforeRename);
        PumpUntil(() => NameDialog(nested.Id) is { IsDisposed: false, Visible: true } && !Field<bool>(context, "_openingNote"));
        fallback = NameDialog(nested.Id)!;
        Field<TextBox>(fallback, "_name").Text = "숨겨진 자료 이름 변경";
        Pump((Task)Invoke(fallback, "SaveAsync")!);
        var renamedNested = nestedBeforeRename with { DisplayName = "숨겨진 자료 이름 변경" };
        assert(fallback.IsDisposed && repository.Get(nested.Id) == renamedNested && Current(Sticker(nested.Id)) == renamedNested &&
            !Sticker(nested.Id).Visible && !Sticker(b.Id).Visible && !repository.Get(folder.Id)!.FolderExpanded,
            "FI22 saving through the hidden-folder fallback persists its name without changing ancestor closure or child visibility");
        Pump((Task)Invoke(context, "ToggleFolderAsync", repository.Get(folder.Id)!)!);
        assert(Listed(nested.Id) == renamedNested && Current(Sticker(nested.Id)) == renamedNested && Sticker(nested.Id).Visible,
            "FI23 opening the ancestor later displays the fallback-renamed folder consistently in the list and sticker views");

        // List requests use independent modeless dialogs even when stickers are available.
        // Reopening one folder must never reuse another folder's draft or commit callback.
        var outerBeforeDialogs = repository.Get(folder.Id)!;
        Field<Action<Bookmark>>(recent, "RenameFolderRequested")(outerBeforeDialogs);
        var outerDialog = NameDialog(folder.Id)!;
        Field<TextBox>(outerDialog, "_name").Text = "첫 번째 폴더 초안";
        Field<Action<Bookmark>>(recent, "RenameFolderRequested")(renamedNested);
        var nestedDialog = NameDialog(nested.Id)!;
        Field<TextBox>(nestedDialog, "_name").Text = "두 번째 폴더 초안";
        assert(!ReferenceEquals(outerDialog, nestedDialog) && outerDialog.Visible && nestedDialog.Visible &&
            Field<TextBox>(outerDialog, "_name").Text == "첫 번째 폴더 초안" &&
            Field<TextBox>(nestedDialog, "_name").Text == "두 번째 폴더 초안" &&
            repository.Get(folder.Id) == outerBeforeDialogs && repository.Get(nested.Id) == renamedNested,
            "FI24 requesting two different folder names keeps independent modeless drafts without committing either one");
        Field<Action<Bookmark>>(recent, "RenameFolderRequested")(outerBeforeDialogs);
        assert(ReferenceEquals(NameDialog(folder.Id), outerDialog) && ReferenceEquals(NameDialog(nested.Id), nestedDialog) &&
            Field<TextBox>(outerDialog, "_name").Text == "첫 번째 폴더 초안" &&
            Field<TextBox>(nestedDialog, "_name").Text == "두 번째 폴더 초안",
            "FI25 requesting the same folder again reuses only that folder's editor and preserves both unsaved drafts");
        Pump((Task)Invoke(nestedDialog, "SaveAsync")!);
        var nestedAfterDialogs = renamedNested with { DisplayName = "두 번째 폴더 초안" };
        assert(nestedDialog.IsDisposed && NameDialog(nested.Id) is null && !outerDialog.IsDisposed &&
            Field<TextBox>(outerDialog, "_name").Text == "첫 번째 폴더 초안" && repository.Get(folder.Id) == outerBeforeDialogs &&
            repository.Get(nested.Id) == nestedAfterDialogs && Listed(nested.Id) == nestedAfterDialogs && Current(Sticker(nested.Id)) == nestedAfterDialogs,
            "FI26 saving the second dialog renames only its own folder in SQLite and both views while retaining the first draft");
        Pump((Task)Invoke(outerDialog, "SaveAsync")!);
        var outerAfterDialogs = outerBeforeDialogs with { DisplayName = "첫 번째 폴더 초안" };
        assert(outerDialog.IsDisposed && NameDialog(folder.Id) is null && repository.Get(folder.Id) == outerAfterDialogs &&
            Listed(folder.Id) == outerAfterDialogs && Current(folderSticker) == outerAfterDialogs && repository.Get(nested.Id) == nestedAfterDialogs,
            "FI27 saving the first retained dialog uses its original folder callback without changing the second folder");

        Task Change(Bookmark item, Guid? target) => (Task)Invoke(context, "ChangeParentAsync", item, target)!;
        Bookmark? Listed(Guid id) => list.Items.Cast<Bookmark>().FirstOrDefault(item => item.Id == id);
        Form[] Forms() => ((System.Collections.IEnumerable)manager.GetType().GetProperty("Forms", Private)!.GetValue(manager)!).Cast<Form>().ToArray();
        Form Sticker(Guid id) => Forms().Single(form => Current(form).Id == id);
        Form? NameDialog(Guid? folderId, Guid? parentId = null)
        {
            var editors = Field<System.Collections.IDictionary>(context, "_folderNameEditors");
            var key = (folderId, parentId);
            return editors.Contains(key) ? (Form)editors[key]! : null;
        }
    }

    private static Bookmark Current(Form form) => (Bookmark)form.GetType().GetProperty("Bookmark")!.GetValue(form)!;
    private static bool Editing(Form form) => (bool)form.GetType().GetProperty("IsEditingNote")!.GetValue(form)!;
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
