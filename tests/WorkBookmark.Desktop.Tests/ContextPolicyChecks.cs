using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using WorkBookmark.App;
using WorkBookmark.Core;
using WorkBookmark.Storage;

namespace WorkBookmark.Desktop.Tests;

/// <summary>Application routes for ordered drops, session undo and draft-protected visibility.</summary>
internal static class ContextPolicyChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string directory, Action<bool, string> assert)
    {
        Directory.CreateDirectory(directory);
        (UserSettings.Default with
        {
            CaptureHotkey = new Hotkey(7, (int)Keys.F17), RecentHotkey = new Hotkey(7, (int)Keys.F18),
            IntroShown = true, DisplayMode = BookmarkDisplayMode.Stickers,
            StickerPresentationVersion = UserSettings.CurrentStickerPresentationVersion
        }).Save(directory);
        bool failNote = false;
        using var repository = new SqliteBookmarkRepository(Path.Combine(directory, "bookmarks.db"), operation =>
        {
            if (failNote && operation == "note") throw new IOException("Synthetic draft-save failure.");
        });
        var folder = repository.CreateFolder("정렬과 저장 게이트");
        repository.SetFolderExpanded(folder.Id, true);
        var first = repository.UpsertCapture(new(TargetKind.File, Path.Combine(directory, "z-last.txt"))).Bookmark;
        var second = repository.UpsertCapture(new(TargetKind.File, Path.Combine(directory, "a-first.txt"))).Bookmark;
        var unrelated = repository.UpsertCapture(new(TargetKind.File, Path.Combine(directory, "unrelated.txt"))).Bookmark;
        repository.SetParent(first.Id, folder.Id);
        repository.SetParent(second.Id, folder.Id);
        using var worker = new WorkerClient();
        using var context = new BookmarkApplicationContext(repository, worker, directory);
        object manager = Field<object>(context, "_stickers");
        var recent = Field<Form>(context, "_recent");
        Application.DoEvents();
        Pump(Call(manager, "SetEnabledAsync", true, false));
        Pump(Call(recent, "OpenAsync"));
        var list = Field<ListBox>(recent, "_items");
        Guid[] OriginalOrder() => BookmarkTree.Flatten(repository.ListActive()).Where(row => row.Bookmark.ParentId == folder.Id)
            .Select(row => row.Bookmark.Id).ToArray();
        Guid[] original = OriginalOrder();
        Guid source = original[^1], target = original[0];
        int index = list.Items.Cast<Bookmark>().ToList().FindIndex(item => item.Id == target);
        list.TopIndex = index;
        Rectangle bounds = list.GetItemRectangle(index);
        Point point = list.PointToScreen(new Point(bounds.Left + 160, bounds.Top + 3));
        var drag = new DragEventArgs(new DataObject("WorkBookmark.BookmarkId", source.ToString()), 0,
            point.X, point.Y, DragDropEffects.Move, DragDropEffects.None);
        Invoke(recent, "ItemsDragOver", list, drag);
        var preview = Field<BookmarkDropRequest?>(recent, "_dropRequest");
        assert(drag.Effect == DragDropEffects.Move && preview is { Kind: BookmarkDropKind.InsertBefore } &&
            preview.TargetId == folder.Id && preview.BeforeId == target && OriginalOrder().SequenceEqual(original),
            "CP01 list row edge previews a sibling insertion without changing committed membership or order");
        Invoke(recent, "ItemsDragDrop", list, drag);
        PumpUntil(() => !Field<bool>(context, "_structureCommandPending"));
        assert(OriginalOrder().SequenceEqual([source, target]) && repository.Get(source)!.ParentId == folder.Id,
            "CP02 releasing the list insertion commits the indicated sibling order through the application");
        Pump(Call(context, "UndoAsync"));
        assert(OriginalOrder().SequenceEqual(original), "CP03 application session undo restores the previous sibling order");

        list.SelectedIndex = list.Items.Cast<Bookmark>().ToList().FindIndex(item => item.Id == folder.Id);
        var sortMenu = Field<ContextMenuStrip>(recent, "_menu").Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "하위 항목 정렬");
        sortMenu.DropDownItems[0].PerformClick();
        PumpUntil(() => !Field<bool>(context, "_structureCommandPending"));
        assert(OriginalOrder().SequenceEqual([second.Id, first.Id]),
            "CP12 the folder list sort menu commits name order through the application");
        Pump(Call(context, "UndoAsync"));
        assert(OriginalOrder().SequenceEqual(original), "CP13 one session undo restores the complete pre-sort sibling order");

        Pump(Call(context, "ApplyDropAsync", new BookmarkDropRequest(first.Id, BookmarkDropKind.Group, second.Id)));
        Guid groupedId = repository.Get(first.Id)!.ParentId!.Value;
        assert(groupedId != folder.Id && repository.Get(groupedId) is { IsFolder: true, FolderExpanded: true } &&
            repository.Get(second.Id)!.ParentId == groupedId,
            "CP04 explicit grouping creates one open folder and commits both members atomically");
        Pump(Call(context, "UndoAsync"));
        assert(repository.Get(first.Id)!.ParentId == folder.Id && repository.Get(second.Id)!.ParentId == folder.Id &&
            repository.Get(groupedId) is null && !Forms(manager).Any(form => Bookmark(form).Id == groupedId) &&
            !list.Items.Cast<Bookmark>().Any(item => item.Id == groupedId),
            "CP05 undo removes a newly created group from storage and both views without losing either member");

        string originalNote = repository.Get(first.Id)!.Note;
        repository.UpdateNote(first.Id, "저장된 메모 변경");
        Pump(Call(context, "RefreshBookmarkAsync", first.Id));
        Pump(Call(context, "UndoAsync"));
        assert(repository.Get(first.Id)!.Note == originalNote && Bookmark(View(first.Id)).Note == originalNote,
            "CP06 global session undo restores committed note content in storage and the sticker");

        Pump(Call(context, "EditNoteAsync", first.Id));
        Form editorView = View(first.Id);
        var editor = Field<TextBox>(editorView, "_noteEditor");
        editor.Text = "사라지면 안 되는 초안";
        Pump(Call(context, "UndoAsync"));
        assert(Editing(editorView) && editor.Text == "사라지면 안 되는 초안" && repository.Get(first.Id)!.Note == originalNote,
            "CP07 global undo waits for an active draft to be saved or cancelled instead of saving and undoing that draft");
        failNote = true;
        Pump(Call(context, "ToggleFolderAsync", repository.Get(folder.Id)!));
        assert(repository.Get(folder.Id)!.FolderExpanded && editorView.Visible && Editing(editorView) &&
            editor.Text == "사라지면 안 되는 초안",
            "CP08 a failed child draft save aborts ancestor collapse and keeps the editor visible");
        Pump(Call(context, "RequestExitAsync"));
        assert(!Field<bool>(context, "_exiting") && editorView.Visible && Editing(editorView),
            "CP09 explicit exit remains open when an active draft cannot be saved");
        failNote = false;
        Pump(Call(context, "ToggleFolderAsync", repository.Get(folder.Id)!));
        assert(!repository.Get(folder.Id)!.FolderExpanded && repository.Get(first.Id)!.Note == "사라지면 안 되는 초안" &&
            !editorView.Visible && !Editing(editorView),
            "CP10 retry saves the draft before committing ancestor collapse");

        Pump(Call(context, "DeleteAsync", repository.Get(folder.Id)!));
        Pump(Call(context, "EditNoteAsync", first.Id));
        editorView = View(first.Id);
        Field<TextBox>(editorView, "_noteEditor").Text = "부모 복원 전에 저장할 초안";
        failNote = true;
        Pump(Call(context, "RestoreBookmarkAsync", folder.Id));
        assert(repository.Get(folder.Id)!.DeletedAtUtc is not null && editorView.Visible && Editing(editorView),
            "CP14 restoring a closed deleted parent waits for the surviving child's draft and aborts on save failure");
        failNote = false;
        Pump(Call(context, "RestoreBookmarkAsync", folder.Id));
        assert(repository.Get(folder.Id)!.DeletedAtUtc is null && !repository.Get(folder.Id)!.FolderExpanded &&
            repository.Get(first.Id)!.Note == "부모 복원 전에 저장할 초안" && !editorView.Visible,
            "CP15 a successful retry saves the orphan draft before restoring its closed ancestor");
        Rectangle anchor = View(folder.Id).Bounds;
        Pump(Call(manager, "HideAllAsync"));
        Pump(Call(context, "ShowStickerAsync", repository.Get(first.Id)!));
        assert(repository.Get(folder.Id)!.FolderExpanded && View(folder.Id).Visible && View(first.Id).Visible &&
            !View(unrelated.Id).Visible && View(folder.Id).Bounds == anchor,
            "CP16 explicit show-item opens its ancestors and reveals only its root while preserving the root anchor");
        Pump(Call(context, "RequestExitAsync"));
        assert(Field<bool>(context, "_exiting"), "CP11 explicit exit completes after every draft and pending layout is saved");

        Form View(Guid id) => Forms(manager).Single(form => Bookmark(form).Id == id);
    }

    private static Form[] Forms(object manager) => ((System.Collections.IEnumerable)manager.GetType().GetProperty("Forms", Private)!.GetValue(manager)!).Cast<Form>().ToArray();
    private static Bookmark Bookmark(Form form) => (Bookmark)form.GetType().GetProperty("Bookmark")!.GetValue(form)!;
    private static bool Editing(Form form) => (bool)form.GetType().GetProperty("IsEditingNote")!.GetValue(form)!;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static object? Invoke(object value, string name, params object?[] arguments) => value.GetType().GetMethod(name, Private | BindingFlags.Public)!.Invoke(value, arguments);
    private static Task Call(object value, string name, params object?[] arguments) => (Task)Invoke(value, name, arguments)!;
    private static void Pump(Task task)
    {
        PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }
    private static void PumpUntil(Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done() && timer.Elapsed < TimeSpan.FromSeconds(12)) { Application.DoEvents(); Thread.Sleep(1); }
        if (!done()) throw new TimeoutException("Application interaction policy check timed out.");
    }
}
