using System.Diagnostics;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using WorkBookmark.App;
using WorkBookmark.Core;

namespace WorkBookmark.Desktop.Tests;

internal static class StickerTreeChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type ManagerType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.StickerManager")!;

    internal static void Run(Action<bool, string> assert)
    {
        var parent = Sample("프로젝트 기획", 1);
        var child = Sample("시장 조사", 2);
        var grandchild = Sample("경쟁 서비스 비교", 3);
        var separate = Sample("이번 주 회의", 4);
        Bookmark[] items = [parent, child, grandchild, separate];
        var manager = Create(() => items);
        using var lifetime = (IDisposable)manager;
        Point cursorBefore = Cursor.Position;
        try
        {
            Pump((Task)Call(manager, "SetEnabledAsync", true, false)!);
            var byId = Forms(manager).ToDictionary(form => BookmarkFor(form).Id);
            Form a = byId[parent.Id], b = byId[child.Id], c = byId[grandchild.Id], d = byId[separate.Id];
            Rectangle area = Screen.FromControl(a).WorkingArea;
            a.Location = new Point(area.Left + 60, area.Top + 60);
            b.Location = new Point(a.Left + a.Width + 70, a.Top + 100);
            c.Location = new Point(a.Left, a.Top + 260);
            d.Location = new Point(a.Left + a.Width + 70, a.Top + 350);
            int requested = 0;
            Guid? requestedChild = null, requestedParent = null;
            ManagerType.GetEvent("ParentChangeRequested", Private)!.GetAddMethod(true)!.Invoke(manager,
                [(Action<Bookmark, Guid?>)((value, newParent) =>
                {
                    requested++;
                    requestedChild = value.Id;
                    requestedParent = newParent;
                })]);
            assert(Forms(manager).All(form => !Field<Label>(form, "_treeMark").Visible),
                "STR01 flat stickers retain the original one-row title and shortcut presentation");

            Cursor.Position = Center(a);
            SendMessage(b.Handle, 0x0231, 0, 0);
            Rectangle drop = SendMoving(b, new Rectangle(a.Location + new Size(8, 4), b.Size));
            assert(Field<bool>(a, "_dropTarget") && drop.Location == a.Location + new Size(8, 4),
                "STR02 a valid parent highlights and accepts overlap ahead of magnetic snapping");
            b.Bounds = drop;
            SendMessage(b.Handle, 0x0232, 0, 0);
            assert(requested == 1 && requestedChild == child.Id && requestedParent == parent.Id &&
                BookmarkFor(b).ParentId is null && !Field<bool>(a, "_dropTarget"),
                "STR03 a completed native drop requests persistence without applying an optimistic parent");
            Size aSize = a.Size, bSize = b.Size;
            child = child with { ParentId = parent.Id };
            items = [parent, child, grandchild, separate];
            Call(manager, "Upsert", child);
            assert(BookmarkFor(b).ParentId == parent.Id && b.Left > a.Left && b.Top > a.Bottom &&
                a.Size == aSize && b.Size == bSize && Field<Label>(a, "_treeMark").Visible &&
                Field<Label>(b, "_treeMark").AccessibleName!.Contains(parent.Note, StringComparison.Ordinal),
                "STR04 a committed parent displays hierarchy and arranges children in indented rows without resizing");

            grandchild = grandchild with { ParentId = child.Id };
            items = [parent, child, grandchild, separate];
            Call(manager, "Upsert", grandchild);
            assert(c.Left > b.Left && c.Top > b.Bottom &&
                Call(manager, "FindDropTarget", a, Center(c), Keys.None) is null &&
                Call(manager, "FindDropTarget", b, Center(a), Keys.None) is null &&
                Call(manager, "FindDropTarget", d, Center(a), Keys.Alt) is null,
                "STR05 nested children indent and reject cycles, unchanged parents, and Alt grouping");
            c.Hide();
            assert(Call(manager, "FindDropTarget", d, Center(c), Keys.None) is null,
                "STR06 hidden stickers cannot receive a drop");

            Point oldA = a.Location, oldB = b.Location, oldC = c.Location;
            var delta = new Size(37, 24);
            Cursor.Position = new Point(area.Right - 2, area.Bottom - 2);
            Move(manager, a, oldA + delta, alt: true);
            assert(a.Location == oldA + delta && b.Location == oldB + delta && c.Location == oldC + delta &&
                !c.Visible && requested == 1,
                "STR07 moving a parent moves its complete subtree and preserves individually hidden descendants");
            oldA = a.Location; oldB = b.Location; oldC = c.Location;
            SendMessage(a.Handle, 0x0231, 0, 0);
            a.Bounds = SendMoving(a, new Rectangle(a.Location + new Size(34, 16), a.Size));
            a.Location = oldA; // The native modal move loop restores this rectangle on Esc.
            SendMessage(a.Handle, 0x0232, 0, 0);
            assert(a.Location == oldA && b.Location == oldB && c.Location == oldC && requested == 1,
                "STR08 cancelling native movement restores descendant positions and does not change hierarchy");
            SendMessage(a.Handle, 0x0231, 0, 0);
            a.Width += 10;
            SendMessage(a.Handle, 0x0232, 0, 0);
            assert(requested == 1 && BookmarkFor(b).ParentId == parent.Id,
                "STR09 native resize completion never requests a parent change");

            c.Show();
            Call(c, "BeginNoteEdit", (Func<string, Task>)(_ => Task.FromException(new IOException("Injected note failure"))));
            Field<TextBox>(c, "_noteEditor").Text = "그룹 이동 중 보존할 초안";
            Point resting = Property<Rectangle>(c, "PlacementBounds").Location;
            Point origin = a.Location;
            delta = new Size(23, 12);
            Move(manager, a, origin + delta, alt: true);
            assert(Property<bool>(c, "IsEditingNote") && Field<TextBox>(c, "_noteEditor").Text == "그룹 이동 중 보존할 초안" &&
                Property<Rectangle>(c, "PlacementBounds").Location == resting + delta,
                "STR10 moving a subtree preserves editing drafts and moves the normal note placement by the same amount");
            Call(c, "CancelNoteEdit");
            assert(c.Location == resting + delta,
                "STR11 leaving note editing restores the correctly moved normal sticker location");

            Call(b, "UpdateMenu");
            Field<ToolStripMenuItem>(b, "_detachMenu").PerformClick();
            assert(requested == 2 && requestedChild == child.Id && requestedParent is null && BookmarkFor(b).ParentId == parent.Id,
                "STR12 the detach menu requests an authoritative root move without mutating the existing relationship");
            child = child with { ParentId = null };
            items = [parent, child, grandchild, separate];
            Call(manager, "Upsert", child);
            assert(!Field<Label>(a, "_treeMark").Visible && Field<Label>(b, "_treeMark").Visible &&
                BookmarkFor(c).ParentId == child.Id && c.Left > b.Left,
                "STR13 detaching a parent keeps its subtree together and removes the former parent's hierarchy cue");

            var arrangement = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.StickerArrangementMode")!;
            Pump((Task)Call(manager, "ArrangeAsync", Enum.Parse(arrangement, "Grid"))!);
            assert(requested == 2 && BookmarkFor(c).ParentId == child.Id && c.Left > b.Left && c.Top > b.Bottom,
                "STR14 explicit arrangement keeps groups in parent-first indented rows without hierarchy writes");

            // No saved positions exist for a tree created entirely in list mode.
            var fresh = Create(() => items);
            using var freshLifetime = (IDisposable)fresh;
            Pump((Task)Call(fresh, "SetEnabledAsync", true, false)!);
            var freshForms = Forms(fresh).ToDictionary(form => BookmarkFor(form).Id);
            assert(freshForms[grandchild.Id].Left > freshForms[child.Id].Left &&
                freshForms[grandchild.Id].Top > freshForms[child.Id].Bottom,
                "STR15 the first sticker reveal lays out a tree created in list mode");

            LayoutChecks(assert);
            PersistenceChecks(assert);
        }
        finally { Cursor.Position = cursorBefore; }
    }

    internal static void Render(string directory)
    {
        Directory.CreateDirectory(directory);
        var parent = Sample("신규 서비스 기획", 1);
        var first = Sample("시장 조사 자료", 4) with { ParentId = parent.Id };
        var nested = Sample("경쟁 서비스 비교표", 3) with { ParentId = first.Id };
        var second = Sample("화면 설계 · 다음 회의에서 검토", 2) with { ParentId = parent.Id };
        var separate = Sample("이번 주 할 일", 5);
        var manager = Create(() => [parent, first, nested, second, separate]);
        using var lifetime = (IDisposable)manager;
        Pump((Task)Call(manager, "SetEnabledAsync", true, false)!);
        var forms = Forms(manager).ToDictionary(form => BookmarkFor(form).Id);
        Form root = forms[parent.Id];
        Rectangle area = Screen.FromControl(root).WorkingArea;
        root.Location = area.Location + new Size(40, 40);
        Call(manager, "LayoutGroup", parent.Id);
        forms[separate.Id].Location = new Point(root.Left, forms[second.Id].Bottom + 35);
        Draw("sticker-tree.png");
        Call(root, "SetDropTarget", true);
        Draw("sticker-tree-drop.png");

        void Draw(string name)
        {
            var all = forms.Values.ToArray();
            Rectangle union = all.Select(form => form.Bounds).Aggregate(Rectangle.Union);
            using var image = new Bitmap(union.Width + 40, union.Height + 40);
            using Graphics graphics = Graphics.FromImage(image);
            graphics.Clear(Color.FromArgb(238, 241, 242));
            foreach (var form in all)
            {
                using var card = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(card, new Rectangle(Point.Empty, card.Size));
                graphics.DrawImageUnscaled(card, form.Left - union.Left + 20, form.Top - union.Top + 20);
            }
            image.Save(Path.Combine(directory, name), ImageFormat.Png);
        }
    }

    private static void LayoutChecks(Action<bool, string> assert)
    {
        var type = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.StickerTreeLayout")!;
        var arrange = type.GetMethod("Arrange", BindingFlags.Static | BindingFlags.NonPublic)!;
        (Size, int)[] rows = [(new Size(270, 50), 0), (new Size(310, 55), 1), (new Size(280, 45), 2)];
        var area = new Rectangle(-1200, 0, 1000, 800);
        var bounds = (IReadOnlyList<Rectangle>)arrange.Invoke(null, [rows, new Point(-230, 760), area, 144])!;
        assert(bounds.All(area.Contains) && bounds[1].Left - bounds[0].Left == 30 &&
            bounds[2].Left - bounds[1].Left == 30 && bounds[1].Top - bounds[0].Bottom == 12 &&
            bounds.Select(value => value.Size).SequenceEqual(rows.Select(row => row.Item1)),
            "STR16 group layout respects scaled indentation, negative monitor coordinates, screen edges and existing sizes");
        rows = Enumerable.Range(0, 50).Select(index => (new Size(270, 50), index % 3)).ToArray();
        bounds = (IReadOnlyList<Rectangle>)arrange.Invoke(null, [rows, area.Location, area, 96])!;
        assert(bounds.All(area.Contains) && bounds.Select(value => value.Location).Distinct().Count() > 20,
            "STR17 tall groups continue in reachable columns instead of piling every overflow child on the bottom edge");
    }

    private static void PersistenceChecks(Action<bool, string> assert)
    {
        var parent = Sample("목록에서 만든 프로젝트", 1);
        var child = Sample("저장된 스티커 위치가 있는 자료", 2);
        Bookmark[] items = [parent, child];
        var layouts = new ConcurrentDictionary<Guid, StickerLayout>();
        void Save(IReadOnlyList<StickerLayout> batch)
        {
            foreach (var layout in batch) layouts[layout.BookmarkId] = layout;
        }
        object NewManager() => Create(() => items, () => layouts.Values.ToArray(), Save);
        Rectangle parentBounds, childCustom;
        var first = NewManager();
        using ((IDisposable)first)
        {
            Pump((Task)Call(first, "SetEnabledAsync", true, false)!);
            var forms = Forms(first).ToDictionary(form => BookmarkFor(form).Id);
            Form a = forms[parent.Id], b = forms[child.Id];
            var area = Screen.FromControl(a).WorkingArea;
            a.Size += new Size(35, 12);
            a.Location = area.Location + new Size(80, 70);
            b.Size += new Size(19, 6);
            b.Location = area.Location + new Size(470, 250);
            Call(a, "OnResizeEnd", EventArgs.Empty);
            Call(b, "OnResizeEnd", EventArgs.Empty);
            parentBounds = a.Bounds;
            childCustom = b.Bounds;
        }
        // A parent edit occurs entirely in list mode after the first manager exits.
        child = child with { ParentId = parent.Id };
        items = [parent, child];
        var second = NewManager();
        using ((IDisposable)second)
        {
            Pump((Task)Call(second, "SetEnabledAsync", true, false)!);
            var forms = Forms(second).ToDictionary(form => BookmarkFor(form).Id);
            Form a = forms[parent.Id], b = forms[child.Id];
            assert(a.Bounds == parentBounds && b.Size == childCustom.Size && b.Left > a.Left && b.Top > a.Bottom &&
                b.Location != childCustom.Location,
                "STR18 saved flat layouts regroup after a list-only parent edit and restart, preserving custom sizes and the root position");
            b.Location = new Point(a.Left + 130, a.Bottom + 120);
            Call(b, "OnResizeEnd", EventArgs.Empty);
            childCustom = b.Bounds;
        }
        assert(layouts[child.Id].ParentId == parent.Id && layouts[parent.Id].ParentId is null,
            "STR19 sticker persistence records the topology used to arrange each saved position");
        var third = NewManager();
        using ((IDisposable)third)
        {
            Pump((Task)Call(third, "SetEnabledAsync", true, false)!);
            var forms = Forms(third).ToDictionary(form => BookmarkFor(form).Id);
            assert(forms[parent.Id].Bounds == parentBounds && forms[child.Id].Bounds == childCustom &&
                BookmarkFor(forms[child.Id]).ParentId == parent.Id,
                "STR20 unchanged topology preserves custom group placements across restart");
        }
        StickerLayout savedParent = layouts[parent.Id], savedChild = layouts[child.Id];
        bool listEditsRetainPlacement;
        var fourth = NewManager();
        using ((IDisposable)fourth)
        {
            // A new process starts in list mode, so neither sticker exists when
            // committed color/note updates are published to the disabled manager.
            Pump((Task)Call(fourth, "SetEnabledAsync", false, false)!);
            child = child with { Color = BookmarkColor.Blue };
            items = [parent, child];
            Call(fourth, "Upsert", child);
            child = child with { Note = "목록 모드에서 편집한 메모" };
            items = [parent, child];
            Call(fourth, "Upsert", child);
            Pump((Task)Call(fourth, "SetEnabledAsync", true, false)!);
            var forms = Forms(fourth).ToDictionary(form => BookmarkFor(form).Id);
            listEditsRetainPlacement = forms[parent.Id].Bounds == parentBounds && forms[child.Id].Bounds == childCustom &&
                BookmarkFor(forms[child.Id]).Color == BookmarkColor.Blue && BookmarkFor(forms[child.Id]).Note == child.Note;
        }
        assert(listEditsRetainPlacement && layouts[parent.Id] == savedParent && layouts[child.Id] == savedChild,
            "STR21 color and note edits before the first sticker reveal in list mode preserve custom tree positions on screen and in storage");
    }

    private static object Create(Func<IReadOnlyList<Bookmark>> items, Func<IReadOnlyList<StickerLayout>>? layouts = null,
        Action<IReadOnlyList<StickerLayout>>? save = null)
    {
        Func<Task<(IReadOnlyList<Bookmark> Items, IReadOnlyList<StickerLayout> Layouts)>> load = () =>
            Task.FromResult((items(), layouts?.Invoke() ?? Array.Empty<StickerLayout>()));
        return ManagerType.GetConstructors(Private).Single().Invoke([load, save ?? (Action<IReadOnlyList<StickerLayout>>)(_ => { }),
            (Action<string>)(message => throw new InvalidOperationException(message))]);
    }
    private static Bookmark Sample(string note, long sequence) => new(Guid.NewGuid(), new CapturedTarget(TargetKind.File, @"C:\tree\sample.txt"),
        "sample.txt", note, note, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, sequence, null, null, null, null);
    private static Form[] Forms(object manager) => ((System.Collections.IEnumerable)ManagerType.GetProperty("Forms", Private)!.GetValue(manager)!).Cast<Form>().ToArray();
    private static Bookmark BookmarkFor(Form form) => Property<Bookmark>(form, "Bookmark");
    private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name, Private | BindingFlags.Public)!.GetValue(value)!;
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static object? Call(object value, string name, params object[] arguments) => value.GetType().GetMethod(name, Private | BindingFlags.Public)!.Invoke(value, arguments);
    private static Point Center(Form form) => new(form.Left + form.Width / 2, form.Top + form.Height / 2);
    private static void Move(object manager, Form form, Point destination, bool alt)
    {
        var property = form.GetType().GetProperty("AdjustMoveBounds", Private)!;
        var adjust = (Func<Rectangle, Keys, Rectangle>)property.GetValue(form)!;
        property.SetValue(form, (Func<Rectangle, Keys, Rectangle>)((bounds, _) => adjust(bounds, alt ? Keys.Alt : Keys.None)));
        try
        {
            SendMessage(form.Handle, 0x0231, 0, 0);
            form.Bounds = SendMoving(form, new Rectangle(destination, form.Size));
            SendMessage(form.Handle, 0x0232, 0, 0);
        }
        finally { property.SetValue(form, adjust); }
    }
    private static Rectangle SendMoving(Form form, Rectangle proposed)
    {
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRectangle>());
        try
        {
            Marshal.StructureToPtr(new NativeRectangle { Left = proposed.Left, Top = proposed.Top, Right = proposed.Right, Bottom = proposed.Bottom }, pointer, false);
            SendMessage(form.Handle, 0x0216, 0, pointer);
            var result = Marshal.PtrToStructure<NativeRectangle>(pointer);
            return Rectangle.FromLTRB(result.Left, result.Top, result.Right, result.Bottom);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    private static void Pump(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Yield(); }
        if (!task.IsCompleted) throw new TimeoutException("Sticker tree check timed out.");
        task.GetAwaiter().GetResult();
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
