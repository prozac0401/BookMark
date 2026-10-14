using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using WorkBookmark.App;
using WorkBookmark.Core;

namespace WorkBookmark.Desktop.Tests;

internal static class FolderStickerChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type ManagerType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.StickerManager")!;

    internal static void Run(Action<bool, string> assert)
    {
        var folder = Folder("프로젝트 자료", 1);
        var first = Item("시장 조사", 2) with { ParentId = folder.Id, Color = BookmarkColor.Blue };
        var nested = Folder("경쟁 서비스", 3) with { ParentId = folder.Id };
        var deep = Item("비교표", 4) with { ParentId = nested.Id };
        var loose = Item("새 자료", 5);
        var disk = Item("연결된 디스크 폴더", 6) with { Target = new(TargetKind.Folder, @"C:\folder-check") };
        var rows = new Dictionary<Guid, Bookmark>(new[] { folder, first, nested, deep, loose, disk }.Select(item => KeyValuePair.Create(item.Id, item)));
        var saved = new ConcurrentDictionary<Guid, StickerLayout>();
        object manager = Create(() => rows.Values.ToArray(), () => saved.Values.ToArray(), batch =>
        {
            foreach (var layout in batch) saved[layout.BookmarkId] = layout;
        });
        using var lifetime = (IDisposable)manager;
        Point cursorBefore = Cursor.Position;
        try
        {
            Pump((Task)Call(manager, "SetEnabledAsync", true, false)!);
            Form View(Guid id) => Forms(manager).Single(form => BookmarkFor(form).Id == id);
            void Publish(params Bookmark[] changes)
            {
                foreach (var change in changes) rows[change.Id] = change;
                Call(manager, "ApplyFolderChanges", changes, null!);
            }
            Form f = View(folder.Id), a = View(first.Id), g = View(nested.Id), b = View(deep.Id), c = View(loose.Id), d = View(disk.Id);
            assert(f.Visible && c.Visible && d.Visible && !a.Visible && !g.Visible && !b.Visible && Forms(manager).Length == 6,
                "FS01 a closed virtual folder hides descendants while retaining their live view identities");
            assert(Field<Button>(f, "_title").Text == folder.DisplayName &&
                Field<Button>(f, "_resume").AccessibleName == "폴더 열기" && Field<Label>(f, "_treeMark").Visible,
                "FS02 a virtual folder uses its name and a folder action rather than a link or note title");
            bool inline = (bool)Call(manager, "EditNote", first, (Func<string, Task>)(_ => Task.CompletedTask))!;
            assert(!inline && !a.Visible && !Property<bool>(a, "IsEditingNote"),
                "FS03 a note request beneath a closed folder declines inline editing without revealing the child");

            int resumes = 0, toggles = 0, renames = 0;
            Guid? createdParent = null;
            On(manager, "ResumeRequested", (Action<Bookmark>)(_ => resumes++));
            On(manager, "FolderToggleRequested", (Action<Bookmark>)(_ => toggles++));
            On(manager, "RenameFolderRequested", (Action<Bookmark>)(_ => renames++));
            On(manager, "CreateFolderRequested", (Action<Guid?>)(parent => createdParent = parent));
            Field<Button>(f, "_title").PerformClick();
            Field<Button>(f, "_resume").PerformClick();
            Command(f, Keys.Control | Keys.Enter);
            assert(toggles == 3 && resumes == 0 && !BookmarkFor(f).FolderExpanded && !a.Visible,
                "FS04 folder title, action and keyboard activation request a persisted toggle and never resume a worker target");
            Call(f, "SetBusy", true);
            Field<Button>(f, "_title").PerformClick();
            Command(f, Keys.Control | Keys.Enter);
            Call(f, "UpdateMenu");
            Field<ToolStripMenuItem>(f, "_renameFolderMenu").PerformClick();
            assert(toggles == 3 && resumes == 0 && renames == 0,
                "FS05 busy folder actions cannot toggle, rename, or resume");
            Call(f, "SetBusy", false);
            Call(f, "UpdateMenu");
            Field<ToolStripMenuItem>(f, "_renameFolderMenu").PerformClick();
            Field<ToolStripMenuItem>(f, "_createFolderMenu").PerformClick();
            assert(renames == 1 && createdParent == folder.Id && !Field<ToolStripMenuItem>(f, "_copyMenu").Visible &&
                !Field<ToolStripMenuItem>(f, "_noteMenu").Visible,
                "FS06 virtual-folder menus route rename/create and omit nonexistent path and note actions");
            Field<Button>(d, "_resume").PerformClick();
            assert(resumes == 1 && !BookmarkFor(d).IsFolder,
                "FS07 a linked disk folder remains an ordinary resumable bookmark");

            Rectangle[] closedBounds = [f.Bounds, a.Bounds, g.Bounds, b.Bounds];
            folder = folder with { FolderExpanded = true };
            Publish(folder);
            assert(f.Visible && a.Visible && g.Visible && !b.Visible &&
                new[] { f.Bounds, a.Bounds, g.Bounds, b.Bounds }.SequenceEqual(closedBounds),
                "FS08 opening a folder reveals direct children but keeps a nested closed folder's contents hidden without moving windows");
            nested = nested with { FolderExpanded = true };
            Publish(nested);
            assert(b.Visible && b.Bounds == closedBounds[3],
                "FS09 nested folders govern their own expansion and retain saved geometry");
            a.Hide();
            folder = folder with { FolderExpanded = false }; Publish(folder);
            folder = folder with { FolderExpanded = true }; Publish(folder);
            assert(!a.Visible && g.Visible && b.Visible,
                "FS10 closing and reopening a folder does not reveal a child that the user individually hid");
            Pump((Task)Call(manager, "ShowAllAsync", false)!);
            assert(a.Visible && g.Visible && b.Visible,
                "FS11 explicit show-all restores user-hidden children while honoring persisted folder expansion");

            int noteSaves = 0;
            inline = (bool)Call(manager, "EditNote", first, (Func<string, Task>)(_ =>
            {
                noteSaves++;
                return Task.FromException(new IOException("Retain the unsaved folder draft."));
            }))!;
            var editor = Field<TextBox>(a, "_noteEditor");
            editor.Text = "폴더를 닫아도 보존할 작성 중인 메모";
            Rectangle editing = a.Bounds, normal = Property<Rectangle>(a, "PlacementBounds");
            folder = folder with { FolderExpanded = false }; Publish(folder);
            assert(inline && !a.Visible && Property<bool>(a, "IsEditingNote") && editor.Text == "폴더를 닫아도 보존할 작성 중인 메모" && noteSaves == 0,
                "FS12 closing a folder keeps a child's editing draft and does not synthesize a focus-loss save");
            folder = folder with { FolderExpanded = true, Color = BookmarkColor.Purple }; Publish(folder);
            assert(a.Visible && Property<bool>(a, "IsEditingNote") && a.Bounds == editing && Property<Rectangle>(a, "PlacementBounds") == normal &&
                BookmarkFor(f).Color == BookmarkColor.Purple && f.Bounds == closedBounds[0],
                "FS13 reopening and recoloring a folder preserve child drafts, normal placement and folder geometry");
            Call(a, "CancelNoteEdit");
            Call(manager, "HideAll");
            folder = folder with { FolderExpanded = false }; Publish(folder);
            folder = folder with { FolderExpanded = true }; Publish(folder);
            assert(Forms(manager).All(form => !form.Visible),
                "FS14 committed folder toggles never override the hide-all policy");
            Pump((Task)Call(manager, "ShowAllAsync", false)!);

            var area = Screen.FromControl(f).WorkingArea;
            d.Location = area.Location + new Size(60, 70);
            c.Location = area.Location + new Size(d.Width + 130, 70);
            f.Location = area.Location + new Size(60, 370);
            int drops = 0;
            Guid? droppedChild = null, droppedTarget = null;
            On(manager, "ParentChangeRequested", (Action<Bookmark, Guid?>)((child, target) =>
            {
                drops++; droppedChild = child.Id; droppedTarget = target;
            }));
            Cursor.Position = Center(d);
            NativeDrop(c, d.Location + new Size(7, 3));
            assert(drops == 1 && droppedChild == loose.Id && droppedTarget == disk.Id && BookmarkFor(c).ParentId is null,
                "FS15 dropping on an ordinary sticker requests atomic folder grouping without an optimistic parent change");
            Rectangle targetBounds = d.Bounds;
            var grouped = Folder("새 폴더", 7);
            disk = disk with { ParentId = grouped.Id }; loose = loose with { ParentId = grouped.Id };
            foreach (var item in new[] { grouped, disk, loose }) rows[item.Id] = item;
            Call(manager, "ApplyFolderChanges", new[] { grouped, disk, loose }, (Guid?)disk.Id);
            Form h = View(grouped.Id);
            assert(h.Visible && !d.Visible && !c.Visible && h.Location == targetBounds.Location &&
                d.Top > h.Bottom && c.Top > h.Bottom && h.Size == targetBounds.Size,
                "FS16 a committed grouping replaces the target location with a closed folder and lays out its hidden children once");
            Rectangle childBefore = c.Bounds;
            grouped = grouped with { FolderExpanded = true }; Publish(grouped);
            assert(c.Visible && d.Visible && c.Bounds == childBefore,
                "FS17 opening a newly grouped folder reveals its organized children without another layout");
            grouped = grouped with { FolderExpanded = false }; Publish(grouped);
            Cursor.Position = Center(f);
            NativeDrop(h, f.Location + new Size(7, 3));
            assert(drops == 2 && droppedChild == grouped.Id && droppedTarget == folder.Id &&
                BookmarkFor(h).ParentId is null && Call(manager, "FindDropTarget", f, Center(a), Keys.None) is null,
                "FS18 a folder can be dropped into another folder while ancestor-to-descendant drops remain invalid");
            grouped = grouped with { ParentId = folder.Id }; Publish(grouped);
            assert(h.Visible && !c.Visible && !d.Visible && BookmarkFor(f).FolderExpanded,
                "FS19 dropping into an existing folder preserves both folders' expansion state");
            folder = folder with { FolderExpanded = false }; Publish(folder);
            Pump((Task)Call(manager, "ShowAllAsync", false)!);
            assert(!a.Visible && !g.Visible && !b.Visible && !h.Visible && !c.Visible && !d.Visible,
                "FS20 a later authoritative reload does not reveal descendants of a closed folder");
        }
        finally { Cursor.Position = cursorBefore; }
        LateReadCheck(assert);
        PersistenceCheck(assert);
    }

    private static void LateReadCheck(Action<bool, string> assert)
    {
        var folder = Folder("읽기 경합 폴더", 1) with { FolderExpanded = true };
        var child = Item("하위 자료", 2) with { ParentId = folder.Id };
        var items = new[] { folder, child };
        TaskCompletionSource<(IReadOnlyList<Bookmark> Items, IReadOnlyList<StickerLayout> Layouts)>? gate = null;
        object manager = Create(() => items, customLoad: () => gate?.Task ?? Task.FromResult(((IReadOnlyList<Bookmark>)items, (IReadOnlyList<StickerLayout>)Array.Empty<StickerLayout>())));
        using var lifetime = (IDisposable)manager;
        Pump((Task)Call(manager, "SetEnabledAsync", true, false)!);
        gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = (Task)Call(manager, "ShowAllAsync", false)!;
        var old = items;
        folder = folder with { FolderExpanded = false };
        items = [folder, child];
        Call(manager, "ApplyFolderChanges", new[] { folder }, null!);
        var pending = gate;
        gate = null;
        pending.SetResult((old, Array.Empty<StickerLayout>()));
        Pump(delayed);
        assert(!Forms(manager).Single(form => BookmarkFor(form).Id == child.Id).Visible &&
            !BookmarkFor(Forms(manager).Single(form => BookmarkFor(form).Id == folder.Id)).FolderExpanded,
            "FS21 a stale in-flight reveal retries after a committed folder close rather than resurrecting its children");
    }

    private static void PersistenceCheck(Action<bool, string> assert)
    {
        var folder = Folder("저장된 폴더", 1) with { FolderExpanded = true };
        var child = Item("사용자 배치", 2) with { ParentId = folder.Id };
        var items = new[] { folder, child };
        var saved = new ConcurrentDictionary<Guid, StickerLayout>();
        object New() => Create(() => items, () => saved.Values.ToArray(), batch =>
        {
            foreach (var layout in batch) saved[layout.BookmarkId] = layout;
        });
        Rectangle childBounds;
        object first = New();
        using ((IDisposable)first)
        {
            Pump((Task)Call(first, "SetEnabledAsync", true, false)!);
            Form card = Forms(first).Single(form => BookmarkFor(form).Id == child.Id);
            card.Location = Screen.FromControl(card).WorkingArea.Location + new Size(110, 240);
            Call(card, "OnResizeEnd", EventArgs.Empty);
            childBounds = card.Bounds;
            folder = folder with { FolderExpanded = false };
            items = [folder, child];
            Call(first, "ApplyFolderChanges", new[] { folder }, null!);
        }
        StickerLayout before = saved[child.Id];
        object second = New();
        using ((IDisposable)second)
        {
            Pump((Task)Call(second, "SetEnabledAsync", true, false)!);
            Form card = Forms(second).Single(form => BookmarkFor(form).Id == child.Id);
            bool initiallyHidden = !card.Visible;
            folder = folder with { FolderExpanded = true, Color = BookmarkColor.Green };
            items = [folder, child];
            Call(second, "ApplyFolderChanges", new[] { folder }, null!);
            assert(initiallyHidden && card.Visible && card.Bounds == childBounds && saved[child.Id] == before,
                "FS22 restarting with a closed folder and reopening it retains independent child placement and layout persistence");
        }
    }

    internal static void Render(string directory)
    {
        Directory.CreateDirectory(directory);
        var folder = Folder("신규 서비스 기획", 1) with { Color = BookmarkColor.Yellow };
        var child = Item("시장 조사 자료", 4) with { ParentId = folder.Id, Color = BookmarkColor.Blue };
        var nested = Folder("경쟁 서비스 분석", 3) with { ParentId = folder.Id, Color = BookmarkColor.Green };
        var deep = Item("주요 기능 비교표", 2) with { ParentId = nested.Id };
        Bookmark[] rows = [folder, child, nested, deep];
        object manager = Create(() => rows);
        using var lifetime = (IDisposable)manager;
        Pump((Task)Call(manager, "SetEnabledAsync", true, false)!);
        Form root = Forms(manager).Single(form => BookmarkFor(form).Id == folder.Id);
        root.Location = Screen.FromControl(root).WorkingArea.Location + new Size(40, 40);
        Call(manager, "LayoutGroup", folder.Id);
        Draw("folders-closed.png");
        folder = folder with { FolderExpanded = true };
        rows = [folder, child, nested, deep];
        Call(manager, "ApplyFolderChanges", new[] { folder }, null!);
        Draw("folders-open.png");
        nested = nested with { FolderExpanded = true };
        rows = [folder, child, nested, deep];
        Call(manager, "ApplyFolderChanges", new[] { nested }, null!);
        Draw("folders-nested-open.png");
        void Draw(string filename)
        {
            var visible = Forms(manager).Where(form => form.Visible).ToArray();
            Rectangle area = visible.Select(form => form.Bounds).Aggregate(Rectangle.Union);
            using var image = new Bitmap(area.Width + 40, area.Height + 40);
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.FromArgb(238, 241, 242));
            foreach (var form in visible)
            {
                using var card = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(card, new Rectangle(Point.Empty, card.Size));
                graphics.DrawImageUnscaled(card, form.Left - area.Left + 20, form.Top - area.Top + 20);
            }
            image.Save(Path.Combine(directory, filename), ImageFormat.Png);
        }
    }

    private static object Create(Func<IReadOnlyList<Bookmark>> items, Func<IReadOnlyList<StickerLayout>>? layouts = null,
        Action<IReadOnlyList<StickerLayout>>? save = null,
        Func<Task<(IReadOnlyList<Bookmark> Items, IReadOnlyList<StickerLayout> Layouts)>>? customLoad = null)
    {
        Func<Task<(IReadOnlyList<Bookmark> Items, IReadOnlyList<StickerLayout> Layouts)>> load = customLoad ?? (() => Task.FromResult((items(), layouts?.Invoke() ?? Array.Empty<StickerLayout>())));
        return ManagerType.GetConstructors(Private).Single().Invoke([load, save ?? (Action<IReadOnlyList<StickerLayout>>)(_ => { }),
            (Action<string>)(message => throw new InvalidOperationException(message))]);
    }
    private static Bookmark Item(string title, long sequence) => new(Guid.NewGuid(), new CapturedTarget(TargetKind.File, @"C:\folder-check\sample.txt"),
        "sample.txt", title, title, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, sequence, null, null, null, null);
    private static Bookmark Folder(string title, long sequence) => Item(title, sequence) with
    {
        Target = new(TargetKind.BookmarkFolder, ""), Note = "폴더 제목에 표시되면 안 되는 메모", FolderExpanded = false
    };
    private static Form[] Forms(object manager) => ((System.Collections.IEnumerable)ManagerType.GetProperty("Forms", Private)!.GetValue(manager)!).Cast<Form>().ToArray();
    private static Bookmark BookmarkFor(Form form) => Property<Bookmark>(form, "Bookmark");
    private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name, Private | BindingFlags.Public)!.GetValue(value)!;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static object? Call(object value, string name, params object?[] arguments) => value.GetType().GetMethod(name, Private | BindingFlags.Public)!.Invoke(value, arguments);
    private static void On(object value, string name, Delegate handler) => value.GetType().GetEvent(name, Private | BindingFlags.Public)!.GetAddMethod(true)!.Invoke(value, [handler]);
    private static Point Center(Form form) => new(form.Left + form.Width / 2, form.Top + form.Height / 2);
    private static void Command(Form form, Keys keys) => Call(form, "ProcessCmdKey", Message.Create(form.Handle, 0x0100, 0, 0), keys);
    private static void NativeDrop(Form form, Point location)
    {
        SendMessage(form.Handle, 0x0231, 0, 0);
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRectangle>());
        try
        {
            var proposed = new Rectangle(location, form.Size);
            Marshal.StructureToPtr(new NativeRectangle { Left = proposed.Left, Top = proposed.Top, Right = proposed.Right, Bottom = proposed.Bottom }, pointer, false);
            SendMessage(form.Handle, 0x0216, 0, pointer);
            var result = Marshal.PtrToStructure<NativeRectangle>(pointer);
            form.Bounds = Rectangle.FromLTRB(result.Left, result.Top, result.Right, result.Bottom);
        }
        finally { Marshal.FreeHGlobal(pointer); }
        SendMessage(form.Handle, 0x0232, 0, 0);
    }
    private static void Pump(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Yield(); }
        if (!task.IsCompleted) throw new TimeoutException("Folder sticker check timed out.");
        task.GetAwaiter().GetResult();
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
