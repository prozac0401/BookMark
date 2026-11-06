using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using WorkBookmark.App;
using WorkBookmark.Core;

namespace WorkBookmark.Desktop.Tests;

internal static class StickerTreePanelChecks
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Assembly AppAssembly = typeof(BookmarkApplicationContext).Assembly;
    private static readonly Type FormType = AppAssembly.GetType("WorkBookmark.App.UI.StickerForm")!;
    private static readonly Type PanelType = AppAssembly.GetType("WorkBookmark.App.UI.StickerTreePanel")!;
    private static readonly Type LayoutType = AppAssembly.GetType("WorkBookmark.App.StickerTreeLayout")!;
    private static readonly Type SideType = AppAssembly.GetType("WorkBookmark.App.StickerTreeOpeningSide")!;

    internal static void Run(Action<bool, string> assert)
    {
        Geometry(assert);
        using var fixture = new Fixture();
        Form root = fixture.Root, first = fixture.First, second = fixture.Second;
        // Native hit regions describe a displayed tree. Control.Visible is
        // inherited from hidden ancestors, so establish the real show precondition.
        root.Show();
        Rectangle anchor = root.Bounds;
        first.Height = 180;
        bool fits = fixture.SetRows(first, second);
        assert(fits && !first.TopLevel && !second.TopLevel && first.FormBorderStyle == FormBorderStyle.None &&
            first.Height == Scale(first, 36) && first.Width == second.Width,
            "TP01 descendants share a clipped host and discard legacy independent row heights");
        assert(RoundedCard(first) && RoundedCard(second) && root.Region is null && first.BackColor.A == 255 &&
            PanelContains(fixture.Panel, first.PointToScreen(new Point(first.Width / 2, first.Height / 2))) &&
            !PanelContains(fixture.Panel, first.PointToScreen(Point.Empty)) &&
            !PanelContains(fixture.Panel, first.PointToScreen(new Point(first.Width / 2, first.Height + Scale(root, 4)))),
            "TP23 only rounded card faces belong to the native host region; corners and gaps are transparent");
        Rectangle rootCard = root.RectangleToScreen(root.ClientRectangle);
        assert(first.RectangleToScreen(first.ClientRectangle).Left == rootCard.Left &&
            first.RectangleToScreen(first.ClientRectangle).Right == rootCard.Right,
            "TP26 child card edges align with the root's visible client area rather than its invisible resize frame");
        int originalSecondTop = second.Top;
        Call(first, "BeginNoteEdit", (Func<string, Task>)(_ => Task.CompletedTask));
        assert(first.Height == Scale(first, 96) && second.Top > originalSecondTop && first.Bottom <= second.Top && root.Bounds == anchor,
            "TP02 inline editing reflows following siblings without moving the root anchor");
        bool roundedEditor = RoundedCard(first);
        bool childBottom = first.Region?.IsVisible(first.Width / 2, first.Height - 2) == true;
        Point bottomPoint = first.PointToScreen(new Point(first.Width / 2, first.Height - 2));
        bool hostBottom = PanelContains(fixture.Panel, bottomPoint);
        bool editorWidth = first.Width == root.ClientSize.Width;
        if (!roundedEditor || !childBottom || !hostBottom || !editorWidth)
        {
            var editorViewport = (Panel)PanelType.GetField("_viewport", Members)!.GetValue(fixture.Panel)!;
            Console.WriteLine($"TP24 diagnostic: rounded={roundedEditor}; childBottom={childBottom}; hostBottom={hostBottom}; " +
                $"widthMatch={editorWidth}; child={first.Bounds}; rootClient={root.ClientSize}; panel={fixture.Panel.Bounds}; " +
                $"visible(root,panel,child)={root.Visible},{fixture.Panel.Visible},{first.Visible}; " +
                $"viewport={editorViewport.Bounds}/{editorViewport.ClientSize}; scroll={editorViewport.AutoScrollPosition}; " +
                $"hostBottomPoint={fixture.Panel.PointToClient(bottomPoint)}");
        }
        assert(roundedEditor && childBottom && hostBottom && editorWidth,
            "TP24 the rounded card clip grows with the editor instead of clipping its lower actions");
        Call(root, "HideForFolder");
        fixture.Panel.Hide();
        assert(!(bool)Call(fixture.Panel, "ContainsVisiblePoint", bottomPoint)!,
            "TP29 a hidden host cannot expose an expanded editor through its cached native region");
        root.Show();
        fixture.SetRows(first, second);
        bottomPoint = first.PointToScreen(new Point(first.Width / 2, first.Height - 2));
        assert(first.Visible && Get<bool>(first, "IsEditingNote") && first.Height == Scale(first, 96) &&
            (bool)Call(fixture.Panel, "ContainsVisiblePoint", bottomPoint)! && root.Bounds == anchor,
            "TP30 showing the tree rebuilds the expanded editor region before accepting native hits");
        Call(first, "CancelNoteEdit");
        assert(second.Top == originalSecondTop && root.Bounds == anchor,
            "TP03 cancelling an embedded editor restores compact visible rows");
        assert(RoundedCard(first) && !first.Region!.IsVisible(first.Width / 2, Scale(first, 60)),
            "TP25 cancelling an editor restores the compact rounded clip without retaining its expanded region");
        fixture.SetRows(second);
        assert(second.Top == Scale(root, 1) && !first.TopLevel && !first.Visible && first.Parent == second.Parent,
            "TP04 folding compacts visible rows while retaining the hidden child's nonactivating host");
        assert(!PanelContains(fixture.Panel, fixture.Panel.PointToScreen(new Point(second.Width / 2, Scale(root, 60)))),
            "TP27 folded rows do not leave an invisible native hit region behind");
        fixture.SetRows(first, second);
        int width = first.Width;
        Call(first, "SetTreeIndent", 1000);
        assert(first.Width == width && second.Width == width,
            "TP05 deep indentation remains inside the common panel width");

        Call(root, "BeginFolderRename", (Func<string, Task>)(_ => Task.FromException(new IOException("synthetic save failure"))));
        assert(root.Bounds == anchor, "TP06 managed root editing retains its complete anchor geometry");
        var failed = (Task<bool>)Call(root, "CommitPendingEditAsync")!;
        Pump(failed);
        assert(!failed.Result && Get<bool>(root, "IsEditingNote") && root.Bounds == anchor,
            "TP07 a failed edit commit retains the root draft and reports failure to the caller");
        Call(root, "CancelNoteEdit");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Call(root, "BeginFolderRename", (Func<string, Task>)(_ => gate.Task));
        var saving = (Task<bool>)Call(root, "CommitPendingEditAsync")!;
        var waiting = (Task<bool>)Call(root, "CommitPendingEditAsync")!;
        assert(!saving.IsCompleted && !waiting.IsCompleted, "TP08 concurrent edit commits wait for the in-flight save");
        gate.SetResult();
        Pump(Task.WhenAll(saving, waiting));
        assert(saving.Result && waiting.Result && !Get<bool>(root, "IsEditingNote") && root.Bounds == anchor,
            "TP09 successful pending edit completion restores the unchanged root header");
        var many = Enumerable.Range(0, 36).Select(index => Create(Item($"긴 목록 {index + 1}", index + 20))).ToArray();
        try
        {
            var viewport = (Panel)PanelType.GetField("_viewport", Members)!.GetValue(fixture.Panel)!;
            root.Show();
            fixture.SetRows(many);
            Call(many[^1], "BeginNoteEdit", (Func<string, Task>)(_ => Task.CompletedTask));
            Call(fixture.Panel, "EnsureEditorVisible", many[^1]);
            Rectangle input = many[^1].RectangleToScreen(Get<Rectangle>(many[^1], "EditorFocusBounds"));
            assert(fixture.Panel.Bounds.Contains(input) && Screen.FromControl(root).WorkingArea.Contains(fixture.Panel.Bounds) && root.Bounds == anchor &&
                PanelContains(fixture.Panel, new Point(input.Left + input.Width / 2, input.Top + input.Height / 2)),
                "TP16 a long tree scrolls the active input into its bounded viewport without moving the anchor");
            Rectangle viewportScreen = fixture.Panel.RectangleToScreen(viewport.Bounds);
            Rectangle clientScreen = viewport.RectangleToScreen(viewport.ClientRectangle);
            assert(viewport.VerticalScroll.Visible && !viewport.HorizontalScroll.Visible &&
                viewportScreen.Left == rootCard.Left && viewportScreen.Right == rootCard.Right &&
                many[^1].Width == viewport.ClientSize.Width && clientScreen.Right < viewportScreen.Right &&
                PanelContains(fixture.Panel, new Point(clientScreen.Right, viewportScreen.Top + viewportScreen.Height / 2)),
                "TP28 the actual scrollbar stays inside the root card width and remains part of the native host region");
            var cancel = (Button)FormType.GetField("_cancelNote", Members)!.GetValue(many[^1])!;
            cancel.Focus();
            Call(fixture.Panel, "EnsureEditorVisible", many[^1]);
            Rectangle cancelScreen = many[^1].RectangleToScreen(cancel.Bounds);
            assert(fixture.Panel.Bounds.Contains(cancelScreen) &&
                PanelContains(fixture.Panel, new Point(cancelScreen.Left + cancelScreen.Width / 2, cancelScreen.Top + cancelScreen.Height / 2)),
                "TP18 keyboard focus scrolls the editor cancel action into view as well as its input");
            Call(many[^1], "CancelNoteEdit");
            fixture.SetRows(first, second);
            bool compactScroll = viewport.AutoScrollPosition == Point.Empty &&
                !viewport.HorizontalScroll.Visible && !viewport.VerticalScroll.Visible &&
                first.Width == root.ClientSize.Width && second.Width == first.Width;
            if (first.Top != Scale(root, 1) || second.Top <= first.Bottom || root.Bounds != anchor || !compactScroll)
                Console.WriteLine($"TP17 diagnostic: first={first.Bounds}; second={second.Bounds}; root={root.Bounds}; anchor={anchor}; " +
                    $"viewport={viewport.Bounds}; scroll={viewport.AutoScrollPosition}; min={viewport.AutoScrollMinSize}; " +
                    $"client={viewport.ClientSize}; display={viewport.DisplayRectangle}; " +
                    $"bars(horizontal,vertical)={viewport.HorizontalScroll.Visible},{viewport.VerticalScroll.Visible}");
            assert(first.Top == Scale(root, 1) && second.Top > first.Bottom && root.Bounds == anchor && compactScroll,
                "TP17 shrinking a scrolled tree clamps its scroll offset and removes empty content rows");
            fixture.SetRows(first);
            Application.DoEvents();
            assert(first.Width == root.ClientSize.Width &&
                !viewport.HorizontalScroll.Visible && !viewport.VerticalScroll.Visible,
                "TP21 removing the last sibling restores full row width without stale scrollbars");
            fixture.SetRows(first, second);
            Call(first, "BeginNoteEdit", (Func<string, Task>)(_ => Task.CompletedTask));
            Call(first, "CancelNoteEdit");
            Application.DoEvents();
            assert(first.Width == root.ClientSize.Width && second.Width == first.Width &&
                !viewport.HorizontalScroll.Visible && !viewport.VerticalScroll.Visible,
                "TP22 compacting an editor preserves common row width after native scrollbar recalculation");
        }
        finally { foreach (Form form in many) form.Dispose(); }
        int drags = 0;
        Action<object, Point> onDrag = (_, _) => drags++;
        EventInfo dragEvent = FormType.GetEvent("TreeDragRequested", Members)!;
        Delegate dragHandler = Delegate.CreateDelegate(dragEvent.EventHandlerType!, onDrag.Target, onDrag.Method);
        dragEvent.GetAddMethod(true)!.Invoke(root, [dragHandler]);
        Call(root, "DragHeader", root, new MouseEventArgs(MouseButtons.Left, 1, 12, 12, 0));
        Call(root, "ContinueHeaderDrag", root, new MouseEventArgs(MouseButtons.Left, 0, 12, 12, 0));
        assert(drags == 0, "TP19 selecting a managed background does not start a structural drag");
        Call(root, "ContinueHeaderDrag", root, new MouseEventArgs(MouseButtons.Left, 0, 12 + SystemInformation.DragSize.Width * 2, 12, 0));
        assert(drags == 1, "TP20 background dragging begins only after the system movement threshold");
        dragEvent.GetRemoveMethod(true)!.Invoke(root, [dragHandler]);
        fixture.Panel.Dispose();
        assert(!first.IsDisposed && !second.IsDisposed && first.TopLevel && second.TopLevel &&
            first.Region is null && second.Region is null && fixture.Panel.Region is null,
            "TP10 disposing the viewport never disposes manager-owned bookmark views");
    }

    private static void Geometry(Action<bool, string> assert)
    {
        Rectangle area = new(-1200, -100, 1000, 800), root = new(-1000, 0, 280, 36);
        object plan = Plan(root, area, 2000)!;
        Rectangle bounds = Get<Rectangle>(plan, "Bounds");
        assert(bounds.Top == root.Bottom && bounds.Width == root.Width && bounds.Bottom == area.Bottom && area.Contains(bounds),
            "TP11 tall content is capped by the available screen extent without shifting the root");
        Rectangle bottomRoot = root with { Y = area.Bottom - root.Height };
        plan = Plan(bottomRoot, area, 200)!;
        assert(Get<Rectangle>(plan, "Bounds").Bottom == bottomRoot.Top && Get<object>(plan, "Side").ToString() == "Above",
            "TP12 a root at the bottom opens above with unchanged coordinates");
        assert(Plan(bottomRoot, area, 200, Enum.Parse(SideType, "Below")) is null,
            "TP13 a locked opening side cannot silently flip during content edits");
        assert(Plan(new Rectangle(0, 30, 200, 36), new Rectangle(0, 0, 200, 96), 200) is null,
            "TP14 insufficient space on both sides produces no off-screen or alternative overlay");
        object shortPlan = Plan(root, area, 36)!;
        assert(Get<Rectangle>(shortPlan, "Bounds").Height == 38,
            "TP15 the viewport shrinks to visible content instead of reserving hidden rows");
    }

    internal static void MixedDpi(Action<bool, string> assert)
    {
        assert(Application.HighDpiMode == HighDpiMode.PerMonitorV2,
            "TPD01 native monitor transitions run in a PerMonitorV2 process");
        using var fixture = new Fixture();
        fixture.Root.Show();
        fixture.SetRows(fixture.First, fixture.Second);
        Screen origin = Screen.FromControl(fixture.Root);
        var observedDpis = new HashSet<int> { fixture.Root.DeviceDpi };
        var destinations = Screen.AllScreens.Where(screen => screen.DeviceName != origin.DeviceName).Append(origin);
        foreach (var screen in destinations)
        {
            int sourceDpi = fixture.Root.DeviceDpi;
            fixture.Root.Location = screen.WorkingArea.Location + new Size(40, 40);
            Rectangle rootBounds = fixture.Root.Bounds;
            fixture.SetRows(fixture.First, fixture.Second);
            int dpi = fixture.Root.DeviceDpi;
            observedDpis.Add(dpi);
            int border = Scale(fixture.Root, 1), rowHeight = Scale(fixture.Root, 36);
            var expected = new Rectangle(rootBounds.Left, rootBounds.Bottom, rootBounds.Width,
                rowHeight * 2 + Scale(fixture.Root, 8) + border * 2);
            string transition = $"{sourceDpi}->{dpi} DPI ({screen.DeviceName})";
            assert(fixture.Panel.Bounds == expected,
                $"TPD02 {transition}: native panel bounds immediately match the root-derived plan");
            Application.DoEvents();
            assert(fixture.Panel.Bounds == expected && fixture.Root.Bounds == rootBounds,
                $"TPD03 {transition}: no delayed autoscale changes the panel or root anchor");
            assert(fixture.Panel.DeviceDpi == dpi && fixture.First.DeviceDpi == dpi && fixture.Second.DeviceDpi == dpi &&
                fixture.First.Size == new Size(fixture.Root.ClientSize.Width, rowHeight) &&
                fixture.Second.Size == fixture.First.Size && fixture.Second.Top == border + rowHeight + Scale(fixture.Root, 8) &&
                RoundedCard(fixture.First) && RoundedCard(fixture.Second) &&
                fixture.First.PointToScreen(Point.Empty).X == fixture.Root.PointToScreen(Point.Empty).X,
                $"TPD04 {transition}: host and rows share the destination DPI and exact row geometry");
            Point face = fixture.First.PointToScreen(new Point(fixture.First.Width / 2, rowHeight / 2));
            Point gap = fixture.First.PointToScreen(new Point(fixture.First.Width / 2, rowHeight + Scale(fixture.Root, 4)));
            assert((bool)Call(fixture.Panel, "ContainsVisiblePoint", face)! &&
                !(bool)Call(fixture.Panel, "ContainsVisiblePoint", gap)! &&
                !(bool)Call(fixture.Panel, "ContainsVisiblePoint", fixture.First.PointToScreen(Point.Empty))!,
                $"TPD06 {transition}: native hit coverage follows rounded faces and excludes transparent gaps and corners");
        }
        assert(observedDpis.Count > 1,
            "TPD05 the native round trip exercised monitors with different DPI settings");
    }

    internal static void Render(string directory)
    {
        Directory.CreateDirectory(directory);
        using var fixture = new Fixture();
        fixture.Root.Show();
        fixture.SetRows(fixture.First, fixture.Second);
        RenderGroup(fixture, Path.Combine(directory, "tree-panel-compact.png"));
        Call(fixture.First, "BeginNoteEdit", (Func<string, Task>)(_ => Task.CompletedTask));
        Application.DoEvents();
        RenderGroup(fixture, Path.Combine(directory, "tree-panel-editing.png"));
        Call(fixture.First, "CancelNoteEdit");
        Call(fixture.Root, "BeginFolderRename", (Func<string, Task>)(_ => Task.CompletedTask));
        Application.DoEvents();
        RenderGroup(fixture, Path.Combine(directory, "tree-root-editing.png"));
        Call(fixture.Root, "CancelNoteEdit");
        var extra = Enumerable.Range(0, 28).Select(index => Create(Item($"스크롤 자료 {index + 1:00}", index + 10))).ToArray();
        try
        {
            fixture.SetRows(extra);
            Call(extra[^1], "BeginNoteEdit", (Func<string, Task>)(_ => Task.CompletedTask));
            Application.DoEvents();
            RenderGroup(fixture, Path.Combine(directory, "tree-panel-scrolled-editor.png"));
            Call(extra[^1], "CancelNoteEdit");
            Rectangle area = Screen.FromControl(fixture.Root).WorkingArea;
            fixture.Root.Top = area.Bottom - fixture.Root.Height;
            Call(fixture.Panel, "ResetOpeningDirection");
            fixture.SetRows(fixture.First, fixture.Second);
            RenderGroup(fixture, Path.Combine(directory, "tree-panel-above.png"));
        }
        finally
        {
            foreach (Form form in extra) form.Dispose();
        }
    }

    private static void RenderGroup(Fixture fixture, string path)
    {
        // Controlled GDI composition, not a desktop capture: DrawToBitmap omits
        // native window regions and top-level DWM corners/shadows. Apply the
        // actual host Region to reveal the white canvas through transparent gaps;
        // the real desktop and top-level DWM still need native QA.
        Rectangle envelope = Rectangle.Union(fixture.Root.Bounds, fixture.Panel.Bounds);
        using var bitmap = new Bitmap(envelope.Width, envelope.Height);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.White);
        fixture.Root.DrawToBitmap(bitmap, new Rectangle(fixture.Root.Left - envelope.Left, fixture.Root.Top - envelope.Top,
            fixture.Root.Width, fixture.Root.Height));
        fixture.Panel.DrawToBitmap(bitmap, new Rectangle(fixture.Panel.Left - envelope.Left, fixture.Panel.Top - envelope.Top,
            fixture.Panel.Width, fixture.Panel.Height));
        ApplyPanelRegionMask(fixture.Panel, bitmap, envelope);
        bitmap.Save(path, ImageFormat.Png);
    }

    private static void ApplyPanelRegionMask(Form panel, Bitmap bitmap, Rectangle envelope)
    {
        if (panel.Region is null) return;
        Rectangle bounds = panel.Bounds;
        bounds.Offset(-envelope.Left, -envelope.Top);
        using var outside = new Region(bounds);
        using var visible = panel.Region.Clone();
        visible.Translate(bounds.Left, bounds.Top);
        outside.Exclude(visible);
        using var graphics = Graphics.FromImage(bitmap);
        using var canvas = new SolidBrush(Color.White);
        graphics.FillRegion(canvas, outside);
    }

    private sealed class Fixture : IDisposable
    {
        internal Form Root { get; }
        internal Form First { get; }
        internal Form Second { get; }
        internal Form Panel { get; }
        private readonly Rectangle _area;

        internal Fixture()
        {
            var folder = Item("프로젝트 자료", 1) with { Target = new(TargetKind.BookmarkFolder, ""), FolderExpanded = true };
            Root = Create(folder);
            First = Create(Item("첫 번째 자료 · 메모 편집", 2) with { ParentId = folder.Id });
            Second = Create(Item("두 번째 자료 · 같은 폴더", 3) with { ParentId = folder.Id });
            Panel = (Form)Activator.CreateInstance(PanelType, nonPublic: true)!;
            _ = Root.Handle;
            _area = Screen.FromControl(Root).WorkingArea;
            Root.Location = _area.Location + new Size(40, 40);
        }

        internal bool SetRows(params Form[] forms)
        {
            Type tuple = typeof(ValueTuple<,>).MakeGenericType(FormType, typeof(int));
            Array rows = Array.CreateInstance(tuple, forms.Length);
            for (int index = 0; index < forms.Length; index++) rows.SetValue(Activator.CreateInstance(tuple, forms[index], 1), index);
            return (bool)Call(Panel, "SetRows", Root, rows, Screen.FromControl(Root).WorkingArea, false)!;
        }

        public void Dispose()
        {
            Panel.Dispose();
            First.Dispose();
            Second.Dispose();
            Root.Dispose();
        }
    }

    private static Form Create(Bookmark item)
    {
        var form = (Form)Activator.CreateInstance(FormType, item)!;
        FormType.GetProperty("ManagedTreeInteractions", Members)!.SetValue(form, true);
        return form;
    }

    private static Bookmark Item(string title, long sequence) => new(Guid.NewGuid(), new CapturedTarget(TargetKind.File, @"C:\tree-panel-fixture\sample.txt"),
        "sample.txt", title, title, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, sequence, null, null, null, null);
    private static int Scale(Form form, int value) => Math.Max(1, (int)Math.Round(value * form.DeviceDpi / 96d));
    private static bool RoundedCard(Form form) => form.Region is { } region &&
        !region.IsVisible(0, 0) && !region.IsVisible(form.Width - 1, form.Height - 1) &&
        region.IsVisible(form.Width / 2, form.Height / 2) && region.IsVisible(Scale(form, 8), 1);
    private static bool PanelContains(Form panel, Point screenPoint) => panel.Region?.IsVisible(panel.PointToClient(screenPoint)) == true;
    private static object? Plan(Rectangle root, Rectangle area, int height, object? side = null) =>
        LayoutType.GetMethod("PlanViewport", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [root, area, height, 96, side]);
    private static object? Call(object value, string name, params object?[] args) => value.GetType().GetMethod(name, Members)!.Invoke(value, args);
    private static T Get<T>(object value, string name) => (T)value.GetType().GetProperty(name, Members)!.GetValue(value)!;
    private static void Pump(Task task)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Yield(); }
        if (!task.IsCompleted) throw new TimeoutException("Tree panel check timed out.");
        task.GetAwaiter().GetResult();
    }
}
