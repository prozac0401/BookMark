using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using WorkBookmark.App;
using WorkBookmark.Core;

namespace WorkBookmark.Desktop.Tests;

internal static class RecentTreeChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type RecentType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.UI.RecentForm")!;

    internal static void Run(Action<bool, string> assert)
    {
        var parent = Make("프로젝트 계획", 2);
        var child = Make("검토 문서", 40, parent.Id);
        var grandchild = Make("최종 체크리스트", 50, child.Id);
        var other = Make("참고 자료", 1);
        Bookmark[] all = [parent, child, grandchild, other, .. Enumerable.Range(0, 25).Select(index => Make($"최근 업무 {index}", index + 5))];
        var queries = new List<string>();
        Func<string, Task<SearchResults>> load = query => { queries.Add(query); return Task.FromResult(new SearchResults(all, false)); };
        using var form = Create(load);
        Pump((Task)RecentType.GetMethod("OpenAsync")!.Invoke(form, null)!);
        var list = Field<ListBox>(form, "_items");
        int resumed = 0;
        RecentType.GetEvent("ResumeRequested")!.AddEventHandler(form, (Action<Bookmark>)(_ => resumed++));
        var moves = new List<(Guid Child, Guid? Parent)>();
        RecentType.GetEvent("ParentChangeRequested")!.AddEventHandler(form, (Action<Bookmark, Guid?>)((item, destination) => moves.Add((item.Id, destination))));
        assert(list.Items.Count == all.Length && Rows(form)[grandchild.Id].Depth == 2 && Rows(form)[other.Id].Depth == 0,
            "RT01 full recent list retains old roots and groups descendants at their tree depth");

        int parentIndex = Index(list, parent.Id);
        list.TopIndex = parentIndex;
        var expander = (Rectangle)Invoke(form, "ExpanderBounds", parentIndex, 0)!;
        var expandPoint = new Point(expander.Left + expander.Width / 2, expander.Top + expander.Height / 2);
        Mouse(form, "ItemsMouseDown", expandPoint);
        Mouse(form, "ItemsMouseUp", expandPoint);
        assert(!Items(list).Any(item => item.Id == child.Id) && resumed == 0,
            "RT02 expander click collapses the entire branch without resuming a bookmark");

        child = child with { Note = "숨겨진 항목의 수정 메모" };
        RecentType.GetMethod("Replace")!.Invoke(form, [child]);
        all[Array.FindIndex(all, item => item.Id == child.Id)] = child;
        Search(form, "숨겨진 항목");
        assert(Items(list).Select(item => item.Id).SequenceEqual([parent.Id, child.Id]) && Rows(form)[child.Id].Depth == 1,
            "RT03 search sees edits to collapsed descendants and includes their ancestor path");
        Search(form, "최종 체크리스트");
        assert(Items(list).Select(item => item.Id).SequenceEqual([parent.Id, child.Id, grandchild.Id]) && queries.All(query => query == ""),
            "RT04 search expands all matched ancestor paths using a complete active snapshot");
        Search(form, "");
        assert(!Items(list).Any(item => item.Id == child.Id), "RT05 clearing search restores the user's collapsed branches");
        Invoke(form, "ToggleBranch", parent);

        var invalid = Drag(form, parent.Id, grandchild.Id);
        Invoke(form, "ItemsDragOver", list, invalid);
        Invoke(form, "ItemsDragDrop", list, invalid);
        assert(invalid.Effect == DragDropEffects.None && moves.Count == 0,
            "RT06 dropping a parent onto its descendant is rejected without a persistence request");
        var self = Drag(form, child.Id, child.Id);
        Invoke(form, "ItemsDragOver", list, self);
        assert(self.Effect == DragDropEffects.None, "RT07 dropping an item onto itself is rejected");
        list.SelectedIndex = Index(list, child.Id);
        var valid = Drag(form, child.Id, other.Id);
        Invoke(form, "ItemsDragOver", list, valid);
        assert(valid.Effect == DragDropEffects.Move && Field<Guid?>(form, "_dropTargetId") == other.Id,
            "RT08 valid parent target receives a move effect and row highlight");
        Invoke(form, "ItemsDragDrop", list, valid);
        assert(moves.SequenceEqual([(child.Id, (Guid?)other.Id)]) && Items(list).Single(item => item.Id == child.Id).ParentId == parent.Id && resumed == 0,
            "RT09 drop requests persistence without prematurely changing the hierarchy or launching the target");
        Field<HashSet<Guid>>(form, "_collapsed").Add(other.Id);
        child = child with { ParentId = other.Id };
        RecentType.GetMethod("Replace")!.Invoke(form, [child]);
        assert(Rows(form)[child.Id].Depth == 1 && Rows(form)[grandchild.Id].Depth == 2 && !Field<HashSet<Guid>>(form, "_collapsed").Contains(other.Id),
            "RT10 committed reparent reveals the new parent and moves the whole subtree");

        var status = Field<Label>(form, "_status");
        var point = status.PointToScreen(new Point(10, 10));
        var rootDrop = new DragEventArgs(new DataObject("WorkBookmark.BookmarkId", child.Id.ToString()), 0, point.X, point.Y, DragDropEffects.Move, DragDropEffects.None);
        Invoke(form, "RootDragOver", status, rootDrop);
        Invoke(form, "RootDragDrop", status, rootDrop);
        assert(rootDrop.Effect == DragDropEffects.Move && moves.Last() == (child.Id, null),
            "RT11 the footer drop zone requests a move to the root level even with a full list");
        list.SelectedIndex = Index(list, child.Id);
        var detach = Field<ContextMenuStrip>(form, "_menu").Items.Cast<ToolStripItem>().Single(item => item.Text == "최상위로 꺼내기");
        int previousMoves = moves.Count;
        detach.PerformClick();
        assert(moves.Count == previousMoves + 1 && moves.Last() == (child.Id, null), "RT12 context menu provides an equivalent root detach action");

        Point clickPoint = RowPoint(list, child.Id);
        Mouse(form, "ItemsMouseDown", clickPoint);
        Mouse(form, "ItemsMouseMove", new Point(clickPoint.X + 1, clickPoint.Y + 1));
        Mouse(form, "ItemsMouseUp", clickPoint);
        assert(resumed == 1, "RT13 movement below the system drag threshold retains ordinary click-to-resume");
        Mouse(form, "ItemsMouseDown", clickPoint);
        RecentType.GetField("_suppressClick", Private)!.SetValue(form, true);
        Mouse(form, "ItemsMouseUp", clickPoint);
        assert(resumed == 1, "RT14 a drag-suppressed mouse release cannot also resume the selected target");
        RecentType.GetMethod("Remove")!.Invoke(form, [other.Id]);
        assert(Rows(form)[child.Id].Depth == 0 && Rows(form)[grandchild.Id].Depth == 1,
            "RT15 deleting a parent exposes the surviving subtree at root level");
        RecentType.GetMethod("Replace")!.Invoke(form, [other with { CaptureSequence = 90 }]);
        assert(Rows(form)[child.Id].Depth == 1 && Rows(form)[grandchild.Id].Depth == 2,
            "RT16 restoring a missing parent immediately restores its retained child hierarchy");
        int currentCount = list.Items.Count;
        RecentType.GetMethod("Replace")!.Invoke(form, [Make("별도의 새 책갈피", 100)]);
        assert(list.Items.Count == currentCount, "RT17 an unrelated new capture preserves the currently open list");

        list.TopIndex = 3;
        Point edge = new(160, 5);
        Point screenEdge = list.PointToScreen(edge);
        var scrolledDrag = new DragEventArgs(new DataObject("WorkBookmark.BookmarkId", parent.Id.ToString()), 0,
            screenEdge.X, screenEdge.Y, DragDropEffects.Move, DragDropEffects.None);
        Invoke(form, "ItemsDragOver", list, scrolledDrag);
        Guid atPointer = ((Bookmark)list.Items[list.IndexFromPoint(edge)]).Id;
        assert(scrolledDrag.Effect == DragDropEffects.Move && Field<Guid?>(form, "_dropTargetId") == atPointer,
            "RT20 edge auto-scroll highlights the same parent that dropping at the pointer will select");

        LoadRaces(parent, child, assert);
    }

    private static void LoadRaces(Bookmark parent, Bookmark child, Action<bool, string> assert)
    {
        child = child with { ParentId = parent.Id };
        var response = new TaskCompletionSource<SearchResults>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<string, Task<SearchResults>> load = _ => response.Task;
        using var form = Create(load);
        Task initial = (Task)RecentType.GetMethod("ReloadAsync")!.Invoke(form, [false])!;
        var edited = child with { Note = "초기 읽기 뒤에 저장한 메모" };
        RecentType.GetMethod("Replace")!.Invoke(form, [edited]);
        response.SetResult(new SearchResults([parent, child], false));
        Pump(initial);
        var list = Field<ListBox>(form, "_items");
        assert(list.Items.Count == 2 && Items(list).Single(item => item.Id == child.Id).Note == edited.Note,
            "RT18 an edit arriving during initial load is merged without cancelling or losing the loaded list");

        response = new TaskCompletionSource<SearchResults>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task later = (Task)RecentType.GetMethod("ReloadAsync")!.Invoke(form, [true])!;
        RecentType.GetMethod("Remove")!.Invoke(form, [parent.Id]);
        var moved = edited with { ParentId = null };
        RecentType.GetMethod("Replace")!.Invoke(form, [moved]);
        response.SetResult(new SearchResults([parent, child], false));
        Pump(later);
        assert(list.Items.Count == 1 && Items(list).Single().ParentId is null && Rows(form)[child.Id].Depth == 0,
            "RT19 a stale reload cannot resurrect a removed parent or overwrite a newer reparent");
    }

    internal static void Render(string path)
    {
        var parent = Make("신제품 출시 준비", 10) with { Note = "10월 출시 일정과 담당 업무를 함께 정리합니다." };
        var child = Make("출시 자료 검토", 30, parent.Id) with { Note = "소개 문구 수정 후 팀 검토 요청" };
        var grandchild = Make("최종 확인 체크리스트", 40, child.Id) with { Note = "문구 · 링크 · 배포 일정 확인" };
        var second = Make("고객 인터뷰 기록", 20, parent.Id) with { Note = "다음 회의 전에 주요 의견 다시 읽기" };
        var other = Make("다음 주 업무", 1);
        Bookmark[] records = [parent, child, grandchild, second, other];
        Func<string, Task<SearchResults>> load = _ => Task.FromResult(new SearchResults(records, false));
        using var form = Create(load);
        Pump((Task)RecentType.GetMethod("OpenAsync")!.Invoke(form, null)!);
        var list = Field<ListBox>(form, "_items");
        list.SelectedIndex = Index(list, child.Id);
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path, ImageFormat.Png);
    }

    private static Form Create(Func<string, Task<SearchResults>> load) => (Form)Activator.CreateInstance(RecentType, load)!;
    private static Bookmark Make(string name, long sequence, Guid? parent = null)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-sequence);
        return new Bookmark(Guid.NewGuid(), new CapturedTarget(TargetKind.File, @"C:\업무\" + name + ".txt"), name, name, "", now, now, sequence, null, null, null, null, parent);
    }
    private static IReadOnlyList<Bookmark> Items(ListBox list) => list.Items.Cast<Bookmark>().ToArray();
    private static Dictionary<Guid, BookmarkTreeRow> Rows(Form form) => Field<Dictionary<Guid, BookmarkTreeRow>>(form, "_rows");
    private static int Index(ListBox list, Guid id) => Items(list).ToList().FindIndex(item => item.Id == id);
    private static Point RowPoint(ListBox list, Guid id)
    {
        int index = Index(list, id);
        list.TopIndex = index;
        Rectangle bounds = list.GetItemRectangle(index);
        return new Point(bounds.Left + 160, bounds.Top + 40);
    }
    private static DragEventArgs Drag(Form form, Guid id, Guid target)
    {
        var list = Field<ListBox>(form, "_items");
        Point point = list.PointToScreen(RowPoint(list, target));
        return new DragEventArgs(new DataObject("WorkBookmark.BookmarkId", id.ToString()), 0, point.X, point.Y, DragDropEffects.Move, DragDropEffects.None);
    }
    private static void Search(Form form, string query)
    {
        Field<TextBox>(form, "_search").Text = query;
        Field<System.Windows.Forms.Timer>(form, "_searchTimer").Stop();
        Pump((Task)RecentType.GetMethod("ReloadAsync")!.Invoke(form, [true])!);
    }
    private static void Mouse(Form form, string method, Point point) => Invoke(form, method, Field<ListBox>(form, "_items"), new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
    private static T Field<T>(object value, string name) => (T)RecentType.GetField(name, Private)!.GetValue(value)!;
    private static object? Invoke(object value, string method, params object[] arguments) => RecentType.GetMethod(method, Private)!.Invoke(value, arguments);
    private static void Pump(Task task)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("Recent tree operation timed out.");
        task.GetAwaiter().GetResult();
    }
}
