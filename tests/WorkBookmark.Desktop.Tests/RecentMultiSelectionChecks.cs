using System.Drawing;
using System.Reflection;
using WorkBookmark.App;
using WorkBookmark.Core;

namespace WorkBookmark.Desktop.Tests;

internal static class RecentMultiSelectionChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type RecentType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.UI.RecentForm")!;

    internal static void Run(Action<bool, string> assert)
    {
        var folder = Make("원본 폴더", 10, folder: true);
        var first = Make("선택 문서 A", 9, folder.Id);
        var second = Make("선택 문서 B", 8, folder.Id);
        var third = Make("문서 C", 7, folder.Id);
        var destination = Make("이동 폴더", 6, folder: true);
        var loose = Make("독립 문서", 5);
        Bookmark[] records = [folder, first, second, third, destination, loose];
        Func<string, Task<SearchResults>> load = _ => Task.FromResult(new SearchResults(records, false));
        using var form = (Form)Activator.CreateInstance(RecentType, load)!;
        Pump((Task)Invoke(form, "OpenAsync")!);
        var list = Field<ListBox>(form, "_items");
        int launches = 0, toggles = 0;
        var drops = new List<BookmarkDropRequest>();
        RecentType.GetEvent("ResumeRequested")!.AddEventHandler(form, (Action<Bookmark>)(_ => launches++));
        RecentType.GetEvent("FolderToggleRequested")!.AddEventHandler(form, (Action<Bookmark>)(_ => toggles++));
        RecentType.GetEvent("DropRequested")!.AddEventHandler(form, (Action<BookmarkDropRequest>)drops.Add);

        Click(form, first.Id);
        Click(form, second.Id, Keys.Control);
        assert(Selected(form).SetEquals([first.Id, second.Id]) && launches == 1,
            "RMS01 Ctrl click adds another list bookmark without launching it");
        Click(form, first.Id, Keys.Control);
        assert(Selected(form).SetEquals([second.Id]) && launches == 1,
            "RMS02 Ctrl click toggles an already selected bookmark off");
        Click(form, folder.Id, Keys.Control, expander: true);
        assert(Selected(form).SetEquals([folder.Id, second.Id]) && toggles == 0,
            "RMS03 Ctrl click on a folder expander selects it without changing expansion");

        var point = RowPoint(list, second.Id);
        Down(form, point, Keys.None);
        assert(Selected(form).SetEquals([folder.Id, second.Id]),
            "RMS04 pressing an already selected row preserves the group until drag or release");
        Up(form, point);
        assert(Selected(form).SetEquals([second.Id]) && launches == 1,
            "RMS05 releasing a plain selection click reduces the group without launching the row");
        Click(form, first.Id, Keys.Control);
        var data = (IDataObject)Invoke(form, "CreateDragData", second.Id)!;
        assert(data.GetData("WorkBookmark.BookmarkId") as string == second.Id.ToString() &&
            ((string)data.GetData("WorkBookmark.BookmarkIds")!).Split('\n').Select(Guid.Parse).SequenceEqual([first.Id, second.Id]),
            "RMS06 the drag payload snapshots every selected identity in visible tree order");
        var into = Drag(form, data, destination.Id);
        Invoke(form, "ItemsDragOver", list, into);
        assert(into.Effect == DragDropEffects.Move && Field<Label>(form, "_status").Text.Contains("2개 함께 이동", StringComparison.Ordinal),
            "RMS07 multi-drop feedback states how many bookmarks will move");
        Invoke(form, "ItemsDragDrop", list, into);
        assert(drops.Count == 1 && drops[0].Kind == BookmarkDropKind.MoveInto && drops[0].TargetId == destination.Id &&
            drops[0].SourceIds.ToHashSet().SetEquals([first.Id, second.Id]) &&
            Field<List<Bookmark>>(form, "_bookmarks").Single(item => item.Id == first.Id).ParentId == folder.Id,
            "RMS08 one bulk drop request carries both identities and leaves rows unchanged before commit");

        var group = Drag(form, data, loose.Id);
        Invoke(form, "ItemsDragDrop", list, group);
        assert(drops.Count == 2 && drops[1].Kind == BookmarkDropKind.Group && drops[1].SourceIds.Count == 2,
            "RMS09 dropping multiple selections on an ordinary bookmark requests one shared folder");
        var insertion = Drag(form, data, third.Id, after: true);
        Invoke(form, "ItemsDragDrop", list, insertion);
        assert(drops.Count == 3 && drops[2].Kind == BookmarkDropKind.InsertBefore && drops[2].BeforeId is null &&
            drops[2].TargetId == folder.Id && drops[2].SourceIds.Count == 2,
            "RMS10 insertion after a sibling excludes all moving siblings when finding the destination slot");

        Click(form, folder.Id, Keys.Control);
        data = (IDataObject)Invoke(form, "CreateDragData", first.Id)!;
        int beforeRejected = drops.Count;
        var descendant = Drag(form, data, third.Id);
        Invoke(form, "ItemsDragOver", list, descendant);
        Invoke(form, "ItemsDragDrop", list, descendant);
        assert(descendant.Effect == DragDropEffects.None && drops.Count == beforeRejected,
            "RMS11 every selected ancestor participates in descendant target rejection");
        var selectedTarget = Drag(form, data, second.Id, after: true);
        Invoke(form, "ItemsDragOver", list, selectedTarget);
        assert(selectedTarget.Effect == DragDropEffects.None,
            "RMS12 an insertion target inside a selected subtree cannot split that subtree");

        Click(form, folder.Id, Keys.Control);
        data = (IDataObject)Invoke(form, "CreateDragData", first.Id)!;
        var footer = Field<Label>(form, "_status");
        var footerPoint = footer.PointToScreen(new Point(10, 10));
        var detach = new DragEventArgs(data, 0, footerPoint.X, footerPoint.Y, DragDropEffects.Move, DragDropEffects.None);
        Invoke(form, "RootDragOver", footer, detach);
        Invoke(form, "RootDragDrop", footer, detach);
        assert(detach.Effect == DragDropEffects.Move && drops.Last().Kind == BookmarkDropKind.Detach && drops.Last().SourceIds.Count == 2,
            "RMS13 the root footer detaches the entire selected group in one request");

        Invoke(form, "Replace", first with { Note = "선택 후 수정" });
        assert(Selected(form).SetEquals([first.Id, second.Id]),
            "RMS14 ordinary row updates preserve all visible selected identities");
        Search(form, "선택 문서 A");
        assert(Selected(form).SetEquals([first.Id]),
            "RMS15 filtering removes hidden selections so future drags cannot move unseen rows");
        Search(form, "");
        assert(Selected(form).SetEquals([first.Id]),
            "RMS16 clearing a filter does not resurrect discarded selections");
        Click(form, second.Id, Keys.Control);
        data = (IDataObject)Invoke(form, "CreateDragData", first.Id)!;
        Invoke(form, "Remove", second.Id);
        var stale = Drag(form, data, destination.Id);
        beforeRejected = drops.Count;
        Invoke(form, "ItemsDragOver", list, stale);
        Invoke(form, "ItemsDragDrop", list, stale);
        assert(stale.Effect == DragDropEffects.None && drops.Count == beforeRejected && !Selected(form).Contains(second.Id),
            "RMS17 deleting one source during a drag rejects the entire stale payload without a partial move");

        var invalidData = new DataObject("WorkBookmark.BookmarkId", first.Id.ToString());
        invalidData.SetData("WorkBookmark.BookmarkIds", first.Id + "\ninvalid-id");
        var invalid = Drag(form, invalidData, destination.Id);
        Invoke(form, "ItemsDragOver", list, invalid);
        assert(invalid.Effect == DragDropEffects.None,
            "RMS18 a malformed multi-selection payload cannot silently become a single-item move");
    }

    private static Bookmark Make(string name, long order, Guid? parent = null, bool folder = false) => new(Guid.NewGuid(),
        new CapturedTarget(folder ? TargetKind.BookmarkFolder : TargetKind.File, folder ? "" : @"C:\synthetic-list-selection\" + name + ".txt"),
        name, name, "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, order, null, null, null, null, parent, FolderExpanded: folder);
    private static HashSet<Guid> Selected(Form form) => Field<HashSet<Guid>>(form, "_selectedIds");
    private static Point RowPoint(ListBox list, Guid id, bool expander = false, bool after = false)
    {
        int index = list.Items.Cast<Bookmark>().ToList().FindIndex(item => item.Id == id);
        list.TopIndex = index;
        Rectangle row = list.GetItemRectangle(index);
        return new Point(row.Left + (expander ? 12 : 180), after ? row.Bottom - 2 : row.Top + 20);
    }
    private static void Click(Form form, Guid id, Keys modifiers = Keys.None, bool expander = false)
    {
        Point point = RowPoint(Field<ListBox>(form, "_items"), id, expander);
        Down(form, point, modifiers);
        Up(form, point);
    }
    private static void Down(Form form, Point point, Keys modifiers) =>
        Invoke(form, "HandleItemsMouseDown", new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0), modifiers);
    private static void Up(Form form, Point point) =>
        Invoke(form, "ItemsMouseUp", Field<ListBox>(form, "_items"), new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
    private static DragEventArgs Drag(Form form, IDataObject data, Guid target, bool after = false)
    {
        var list = Field<ListBox>(form, "_items");
        Point point = list.PointToScreen(RowPoint(list, target, after: after));
        return new DragEventArgs(data, 0, point.X, point.Y, DragDropEffects.Move, DragDropEffects.None);
    }
    private static void Search(Form form, string query)
    {
        Field<TextBox>(form, "_search").Text = query;
        Field<System.Windows.Forms.Timer>(form, "_searchTimer").Stop();
        Pump((Task)Invoke(form, "ReloadAsync", true)!);
    }
    private static T Field<T>(Form form, string name) => (T)RecentType.GetField(name, Instance)!.GetValue(form)!;
    private static object? Invoke(Form form, string name, params object[] args) => RecentType.GetMethod(name, Instance)!.Invoke(form, args);
    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("Multi-selection list operation timed out.");
        task.GetAwaiter().GetResult();
    }
}
