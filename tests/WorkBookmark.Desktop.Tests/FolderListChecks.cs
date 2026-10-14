using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using WorkBookmark.App;
using WorkBookmark.Core;

namespace WorkBookmark.Desktop.Tests;

internal static class FolderListChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type RecentType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.UI.RecentForm")!;

    internal static void Run(Action<bool, string> assert)
    {
        var folder = Make("출시 프로젝트", 100, isFolder: true);
        var child = Make("검토 문서", 80, folder.Id);
        var nested = Make("고객 자료", 90, folder.Id, isFolder: true);
        var deep = Make("인터뷰 핵심 의견", 85, nested.Id);
        var first = Make("별도 문서 A", 30) with { Color = BookmarkColor.Blue };
        var second = Make("별도 문서 B", 20) with { Color = BookmarkColor.Pink };
        var physical = Make("실제 파일 폴더", 10) with { Target = new CapturedTarget(TargetKind.Folder, @"C:\업무\자료") };
        var records = new[] { folder, child, nested, deep, first, second, physical }.ToDictionary(item => item.Id);
        Func<string, Task<SearchResults>> load = _ => Task.FromResult(new SearchResults(records.Values.ToArray(), false));
        using var form = Create(load);
        Pump((Task)Invoke(form, "OpenAsync")!);
        var list = Field<ListBox>(form, "_items");
        var toggles = new List<Guid>();
        var resumes = new List<Guid>();
        var creates = new List<Guid?>();
        var renames = new List<Guid>();
        var drops = new List<(Guid Source, Guid? Target)>();
        RecentType.GetEvent("FolderToggleRequested")!.AddEventHandler(form, (Action<Bookmark>)(item => toggles.Add(item.Id)));
        RecentType.GetEvent("ResumeRequested")!.AddEventHandler(form, (Action<Bookmark>)(item => resumes.Add(item.Id)));
        RecentType.GetEvent("CreateFolderRequested")!.AddEventHandler(form, (Action<Guid?>)(parent => creates.Add(parent)));
        RecentType.GetEvent("RenameFolderRequested")!.AddEventHandler(form, (Action<Bookmark>)(item => renames.Add(item.Id)));
        RecentType.GetEvent("ParentChangeRequested")!.AddEventHandler(form, (Action<Bookmark, Guid?>)((item, target) => drops.Add((item.Id, target))));
        void Commit(params Bookmark[] values)
        {
            foreach (var value in values) records[value.Id] = value;
            Invoke(form, "ApplyFolderChanges", (object)values);
        }
        void Replace(Bookmark value) { records[value.Id] = value; Invoke(form, "Replace", value); }
        assert(folder.IsFolder && !physical.IsFolder && Items(list).Count == 4 && !Visible(list, nested.Id) && !Visible(list, deep.Id),
            "FL01 committed closed folders hide every descendant while physical folders remain ordinary bookmarks");

        Click(form, folder.Id);
        assert(toggles.SequenceEqual([folder.Id]) && resumes.Count == 0 && !Visible(list, child.Id),
            "FL02 folder title click requests opening without launching a target or changing visibility before commit");
        folder = folder with { FolderExpanded = true };
        Replace(folder);
        assert(Visible(list, child.Id) && Visible(list, nested.Id) && !Visible(list, deep.Id),
            "FL03 committing an open parent reveals its children while nested closed folders stay closed");
        list.SelectedIndex = Index(list, nested.Id);
        Command(form, Keys.Enter);
        assert(toggles.Last() == nested.Id && resumes.Count == 0 && !Visible(list, deep.Id),
            "FL04 Enter on a folder requests its persisted toggle instead of invoking resume");
        nested = nested with { FolderExpanded = true };
        Replace(nested);
        assert(Rows(form)[deep.Id].Depth == 2, "FL05 committing a nested open state reveals descendants at their full depth");
        folder = folder with { FolderExpanded = false };
        Replace(folder);
        assert(!Visible(list, nested.Id) && !Visible(list, deep.Id) && nested.FolderExpanded,
            "FL06 closing an ancestor hides expanded nested folders without changing their saved expansion");

        Search(form, deep.DisplayName);
        assert(Items(list).Select(item => item.Id).SequenceEqual([folder.Id, nested.Id, deep.Id]) && !Items(list).First().FolderExpanded,
            "FL07 search reveals a matching path through closed folders without mutating their saved state");
        int previousToggles = toggles.Count;
        Click(form, folder.Id);
        assert(toggles.Count == previousToggles && resumes.Count == 0 && Field<Label>(form, "_status").Text.Contains("검색 중", StringComparison.Ordinal),
            "FL08 clicking a folder in search preserves the matched ancestor path and explains temporary expansion");
        Search(form, "");
        assert(!Visible(list, child.Id) && !Visible(list, deep.Id), "FL09 clearing search restores committed closed-folder visibility");

        using (var other = new Form())
        {
            other.Show();
            other.Activate();
            Application.DoEvents();
            assert(!form.Visible, "FL26 activating another window still dismisses the recent list");
        }
        Pump((Task)Invoke(form, "OpenAsync")!);

        list.SelectedIndex = Index(list, folder.Id);
        MenuOpening(form);
        var menu = Field<ContextMenuStrip>(form, "_menu");
        assert(Field<ToolStripMenuItem>(form, "_renameFolderMenu").Available && Field<ToolStripMenuItem>(form, "_createFolderMenu").Text == "하위 폴더 만들기" &&
            !menu.Items.Cast<ToolStripItem>().Single(item => item.Text == "전체 경로/URL 복사").Available &&
            !menu.Items.Cast<ToolStripItem>().Single(item => item.Text == "위치 다시 지정").Available,
            "FL10 folder context provides rename and child-folder creation while excluding filesystem actions");
        Field<ToolStripMenuItem>(form, "_createFolderMenu").PerformClick();
        Field<ToolStripMenuItem>(form, "_renameFolderMenu").PerformClick();
        Field<Button>(form, "_newFolder").PerformClick();
        assert(creates.SequenceEqual([(Guid?)folder.Id, null]) && renames.SequenceEqual([folder.Id]) && Items(list).Count == 4,
            "FL11 create and rename affordances emit the exact parent or folder and await persistence");
        Field<Button>(form, "_newFolder").Focus();
        int previousCreates = creates.Count;
        previousToggles = toggles.Count;
        Command(form, Keys.Enter);
        assert(creates.Count == previousCreates + 1 && creates.Last() is null && toggles.Count == previousToggles && resumes.Count == 0,
            "FL23 Enter on the new-folder toolbar creates a folder without activating the selected row");
        Click(form, physical.Id);
        MenuOpening(form);
        assert(resumes.SequenceEqual([physical.Id]) && !Field<ToolStripMenuItem>(form, "_renameFolderMenu").Available &&
            menu.Items.Cast<ToolStripItem>().Single(item => item.Text == "전체 경로/URL 복사").Available,
            "FL12 clicking a physical folder resumes it and keeps its ordinary path-copy menu");

        list.SelectedIndex = Index(list, first.Id);
        var groupDrop = Drag(form, first.Id, second.Id);
        Invoke(form, "ItemsDragOver", list, groupDrop);
        bool groupingHint = Field<Label>(form, "_status").Text.Contains("새 폴더", StringComparison.Ordinal);
        Invoke(form, "ItemsDragDrop", list, groupDrop);
        assert(groupDrop.Effect == DragDropEffects.Move && groupingHint && drops.Last() == (first.Id, second.Id) && Visible(list, first.Id) && Visible(list, second.Id),
            "FL13 dropping onto an ordinary bookmark requests grouping and leaves both rows unchanged until commit");
        var group = Make("새 폴더", 200, isFolder: true);
        first = first with { ParentId = group.Id };
        second = second with { ParentId = group.Id };
        Commit(first, second, group);
        assert(Items(list).Count(item => item.Id == group.Id) == 1 && !Visible(list, first.Id) && !Visible(list, second.Id) &&
            ((Bookmark)list.SelectedItem!).Id == group.Id && Field<List<Bookmark>>(form, "_bookmarks").Count == 8,
            "FL14 one committed grouping batch inserts the closed folder, hides its members and selects the folder coherently");
        group = group with { FolderExpanded = true };
        Replace(group);
        assert(Rows(form)[first.Id].Depth == 1 && Rows(form)[second.Id].Depth == 1 &&
            Items(list).Single(item => item.Id == first.Id).Color == BookmarkColor.Blue && Items(list).Single(item => item.Id == second.Id).Color == BookmarkColor.Pink,
            "FL15 opening a grouped folder reveals original bookmarks with independent colors intact");

        list.SelectedIndex = Index(list, first.Id);
        var intoFolder = Drag(form, first.Id, folder.Id);
        Invoke(form, "ItemsDragOver", list, intoFolder);
        bool existingHint = Field<Label>(form, "_status").Text.Contains("폴더에 넣습니다", StringComparison.Ordinal);
        Invoke(form, "ItemsDragDrop", list, intoFolder);
        first = first with { ParentId = folder.Id };
        Commit(first, folder);
        assert(existingHint && drops.Last() == (first.Id, folder.Id) && !Visible(list, first.Id) &&
            ((Bookmark)list.SelectedItem!).Id == folder.Id && !Items(list).Single(item => item.Id == folder.Id).FolderExpanded,
            "FL16 dropping into an existing closed folder preserves its expansion and selects the visible ancestor");
        folder = folder with { FolderExpanded = true };
        Replace(folder);
        var cycle = Drag(form, folder.Id, deep.Id);
        int previousDrops = drops.Count;
        Invoke(form, "ItemsDragOver", list, cycle);
        Invoke(form, "ItemsDragDrop", list, cycle);
        assert(cycle.Effect == DragDropEffects.None && drops.Count == previousDrops,
            "FL17 a folder cannot be grouped onto one of its own descendants");

        var empty = Make("비어 있는 폴더", 300, isFolder: true);
        Commit(empty);
        list.SelectedIndex = Index(list, empty.Id);
        Command(form, Keys.Enter);
        assert(toggles.Last() == empty.Id && resumes.SequenceEqual([physical.Id]),
            "FL18 an empty folder still opens through Enter without requiring a child or launching anything");
        empty = empty with { FolderExpanded = true, DisplayName = "정리할 자료" };
        Replace(empty);
        assert(Items(list).Single(item => item.Id == empty.Id).DisplayName == "정리할 자료" && !Field<HashSet<Guid>>(form, "_collapsed").Contains(empty.Id),
            "FL19 committed rename and expansion update an empty folder independently");

        RaceChecks(assert);
        EmptyListChecks(assert);
    }

    private static void RaceChecks(Action<bool, string> assert)
    {
        var first = Make("읽는 중 문서 A", 2);
        var second = Make("읽는 중 문서 B", 1);
        var folder = Make("읽는 중 만든 폴더", 3, isFolder: true);
        var response = new TaskCompletionSource<SearchResults>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var form = Create(_ => response.Task);
        Task initial = (Task)Invoke(form, "ReloadAsync", false)!;
        first = first with { ParentId = folder.Id };
        second = second with { ParentId = folder.Id };
        Invoke(form, "ApplyFolderChanges", (object)new[] { first, second, folder });
        response.SetResult(new SearchResults([first with { ParentId = null }, second with { ParentId = null }], false));
        Pump(initial);
        var list = Field<ListBox>(form, "_items");
        assert(Items(list).Select(item => item.Id).SequenceEqual([folder.Id]) && Field<List<Bookmark>>(form, "_bookmarks").Count == 3,
            "FL20 a stale initial read cannot remove a newly committed folder batch or restore ungrouped rows");
        response = new TaskCompletionSource<SearchResults>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending = (Task)Invoke(form, "ReloadAsync", true)!;
        Invoke(form, "Replace", folder with { FolderExpanded = true });
        response.SetResult(new SearchResults([first, second, folder], false));
        Pump(pending);
        assert(Items(list).Count == 3 && Items(list).First().FolderExpanded,
            "FL21 a later committed folder toggle wins over an older in-flight closed-folder snapshot");
    }

    private static void EmptyListChecks(Action<bool, string> assert)
    {
        using var form = Create(_ => Task.FromResult(new SearchResults([], false)));
        Pump((Task)Invoke(form, "OpenAsync")!);
        Guid? requested = Guid.NewGuid();
        int creates = 0;
        RecentType.GetEvent("CreateFolderRequested")!.AddEventHandler(form, (Action<Guid?>)(parent => { requested = parent; creates++; }));
        var opening = MenuOpening(form);
        Field<ToolStripMenuItem>(form, "_createFolderMenu").PerformClick();
        assert(!opening.Cancel && creates == 1 && requested is null && Field<ListBox>(form, "_items").Items.Count == 0,
            "FL22 an empty list still offers root-folder creation without an existing bookmark");
    }

    internal static void Render(string directory)
    {
        Directory.CreateDirectory(directory);
        var folder = Make("신제품 출시 준비", 100, isFolder: true) with { Color = BookmarkColor.Yellow };
        var child = Make("소개 자료 검토", 80, folder.Id) with { Color = BookmarkColor.Blue, Note = "최신 문구와 배포 링크를 확인합니다." };
        var nested = Make("고객 인터뷰", 70, folder.Id, isFolder: true) with { Color = BookmarkColor.Pink };
        var deep = Make("후속 질문", 60, nested.Id);
        var loose = Make("다음 주 업무", 10);
        Bookmark[] records = [folder, child, nested, deep, loose];
        using var form = Create(_ => Task.FromResult(new SearchResults(records, false)));
        Pump((Task)Invoke(form, "OpenAsync")!);
        Save(form, Path.Combine(directory, "list-folders-closed.png"));
        Invoke(form, "Replace", folder with { FolderExpanded = true });
        Save(form, Path.Combine(directory, "list-folders-open.png"));
    }

    private static Form Create(Func<string, Task<SearchResults>> load) => (Form)Activator.CreateInstance(RecentType, load)!;
    private static Bookmark Make(string title, long sequence, Guid? parent = null, bool isFolder = false)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-sequence);
        return new Bookmark(Guid.NewGuid(), new CapturedTarget(isFolder ? TargetKind.BookmarkFolder : TargetKind.File, isFolder ? "" : @"C:\업무\" + title + ".txt"),
            title, title, "", now, now, sequence, null, null, null, null, parent);
    }
    private static List<Bookmark> Items(ListBox list) => list.Items.Cast<Bookmark>().ToList();
    private static bool Visible(ListBox list, Guid id) => Items(list).Any(item => item.Id == id);
    private static int Index(ListBox list, Guid id) => Items(list).FindIndex(item => item.Id == id);
    private static Dictionary<Guid, BookmarkTreeRow> Rows(Form form) => Field<Dictionary<Guid, BookmarkTreeRow>>(form, "_rows");
    private static Point RowPoint(ListBox list, Guid id)
    {
        int index = Index(list, id);
        list.TopIndex = index;
        Rectangle bounds = list.GetItemRectangle(index);
        return new Point(bounds.Left + 180, bounds.Top + 40);
    }
    private static void Click(Form form, Guid id)
    {
        var list = Field<ListBox>(form, "_items");
        list.Focus();
        var point = RowPoint(list, id);
        var mouse = new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0);
        Invoke(form, "ItemsMouseDown", list, mouse);
        Invoke(form, "ItemsMouseUp", list, mouse);
    }
    private static DragEventArgs Drag(Form form, Guid id, Guid target)
    {
        var list = Field<ListBox>(form, "_items");
        Point point = list.PointToScreen(RowPoint(list, target));
        return new DragEventArgs(new DataObject("WorkBookmark.BookmarkId", id.ToString()), 0, point.X, point.Y, DragDropEffects.Move, DragDropEffects.None);
    }
    private static CancelEventArgs MenuOpening(Form form)
    {
        var args = new CancelEventArgs();
        typeof(ToolStripDropDown).GetMethod("OnOpening", Private)!.Invoke(Field<ContextMenuStrip>(form, "_menu"), [args]);
        return args;
    }
    private static void Search(Form form, string query)
    {
        Field<TextBox>(form, "_search").Text = query;
        Field<System.Windows.Forms.Timer>(form, "_searchTimer").Stop();
        Pump((Task)Invoke(form, "ReloadAsync", true)!);
    }
    private static void Command(Form form, Keys keys)
    {
        var message = Message.Create(form.Handle, 0, nint.Zero, nint.Zero);
        RecentType.GetMethod("ProcessCmdKey", Private)!.Invoke(form, [message, keys]);
    }
    private static void Save(Form form, string path)
    {
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path, ImageFormat.Png);
    }
    private static T Field<T>(object value, string name) => (T)RecentType.GetField(name, Private)!.GetValue(value)!;
    private static object? Invoke(object value, string method, params object[] args) => RecentType.GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(value, args);
    private static void Pump(Task task)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("Folder list operation timed out.");
        task.GetAwaiter().GetResult();
    }
}
