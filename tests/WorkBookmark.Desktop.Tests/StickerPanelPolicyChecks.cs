using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using WorkBookmark.App;
using WorkBookmark.Core;

namespace WorkBookmark.Desktop.Tests;

/// <summary>Behavioral checks for the manager and shaped card viewport, using isolated in-memory data.</summary>
internal static class StickerPanelPolicyChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly Type ManagerType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.StickerManager")!;

    internal static void Run(Action<bool, string> assert)
    {
        var root = Folder("패널 프로젝트", 10);
        var first = Item("첫 번째 자료", 3) with { ParentId = root.Id, SiblingOrder = 0 };
        var nested = Folder("하위 폴더", 2) with { ParentId = root.Id, SiblingOrder = 1 };
        var deep = Item("안쪽 자료", 1) with { ParentId = nested.Id, SiblingOrder = 0 };
        var last = Item("마지막 자료", 4) with { ParentId = root.Id, SiblingOrder = 2 };
        var peer = Item("움직이지 않을 다른 스티커", 20);
        using var fixture = new Fixture(root, first, nested, deep, last, peer);
        var r = fixture.View(root.Id); var a = fixture.View(first.Id); var n = fixture.View(nested.Id);
        var d = fixture.View(deep.Id); var b = fixture.View(last.Id); var p = fixture.View(peer.Id);
        Rectangle area = Screen.FromControl(r).WorkingArea;
        r.Location = new Point(area.Left + 40, area.Top + 40);
        p.Location = new Point(r.Left, r.Bottom + 20);
        Call(r, "OnResizeEnd", EventArgs.Empty);
        Call(p, "OnResizeEnd", EventArgs.Empty);
        fixture.Flush();
        Rectangle rootBefore = r.Bounds, peerBefore = p.Bounds;
        fixture.Publish(root with { FolderExpanded = true });
        var panel = fixture.Panel(root.Id);
        assert(panel.Visible && !a.TopLevel && !n.TopLevel && !b.TopLevel && !d.Visible &&
               a.Parent == n.Parent && n.Parent == b.Parent,
            "SP01 visible children share one shaped viewport and closed descendants stay hidden");
        assert(r.Bounds == rootBefore && p.Bounds == peerBefore,
            "SP02 opening a folder never displaces its root or an overlapping external sticker");
        assert(ScreenBounds(a).Bottom <= ScreenBounds(n).Top && ScreenBounds(n).Bottom <= ScreenBounds(b).Top,
            "SP03 sibling rows follow persisted order without independent coordinates");

        int lastTop = ScreenBounds(b).Top;
        fixture.Publish(nested with { FolderExpanded = true });
        assert(d.Visible && !d.TopLevel && ScreenBounds(b).Top > lastTop && p.Bounds == peerBefore,
            "SP04 nested expansion grows only its own content and preserves external positions");
        fixture.Publish(nested);
        assert(!d.Visible && ScreenBounds(b).Top == lastTop && r.Bounds == rootBefore,
            "SP05 folding removes the hidden row and closes its gap without moving the root");

        int nextTop = ScreenBounds(n).Top;
        Func<string, Task> fail = _ => Task.FromException(new IOException("isolated save failure"));
        Call(fixture.Manager, "EditNote", first, fail);
        assert(Property<bool>(a, "IsEditingNote") && ScreenBounds(n).Top > nextTop &&
               ScreenBounds(a).Bottom <= ScreenBounds(n).Top && p.Bounds == peerBefore && r.Bounds == rootBefore,
            "SP06 editing reflows following siblings inside the panel while peer/root positions remain fixed");
        bool hidden = Pump((Task<bool>)Call(fixture.Manager, "HideTreeAsync", first.Id)!);
        assert(!hidden && r.Visible && panel.Visible && Property<bool>(a, "IsEditingNote"),
            "SP07 failed draft storage prevents hiding the containing tree");
        Call(a, "CancelNoteEdit");
        assert(ScreenBounds(n).Top == nextTop,
            "SP08 cancelling an editor returns following rows to their compact positions");
        hidden = Pump((Task<bool>)Call(fixture.Manager, "HideTreeAsync", first.Id)!);
        assert(hidden && !r.Visible && !a.Visible && !n.Visible && !b.Visible && p.Visible,
            "SP09 hiding from a child hides its complete tree and leaves other roots shown");
        Call(fixture.Manager, "ShowTree", first.Id);
        assert(r.Visible && a.Visible && !d.Visible && p.Bounds == peerBefore,
            "SP10 revealing a tree restores its folder states with no orphan child or peer movement");

        Call(fixture.Manager, "BringTreeToFront", root.Id);
        Point gap = new(r.Left + r.Width / 2, ScreenBounds(a).Bottom + 3);
        Point peerUnderGap = new(gap.X - p.Width / 2, gap.Y - p.Height / 2);
        p.Location = peerUnderGap;
        Call(fixture.Manager, "BringTreeToFront", root.Id);
        var decision = Call(fixture.Manager, "EvaluateDrop", b, gap, Size.Empty, Keys.None)!;
        assert(Property<bool>(decision, "Valid") && Property<BookmarkDropRequest?>(decision, "Request") is
            { Kind: BookmarkDropKind.Group, TargetId: var gapTarget } && gapTarget == peer.Id,
            "SP11 transparent card gaps target the visible sticker underneath instead of the panel rectangle");
        CheckTransparentCoverage(fixture, r, a, b, p, gap, assert);
        p.Location = peerUnderGap;
        Call(fixture.Manager, "BringTreeToFront", root.Id);
        Point desktop = new(area.Right - 12, area.Bottom - 12);
        decision = Call(fixture.Manager, "EvaluateDrop", b, desktop, new Size(8, 8), Keys.None)!;
        var request = Property<BookmarkDropRequest?>(decision, "Request");
        assert(Property<bool>(decision, "Valid") && request is { Kind: BookmarkDropKind.Detach, RootLayout: not null } &&
               request.BookmarkId == last.Id,
            "SP12 child desktop drop previews a detached root with a concrete saved placement");
        var previewBounds = Property<Rectangle>(decision, "Bounds");
        fixture.Publish(last with { ParentId = null });
        Call(fixture.Manager, "ApplySavedLayouts", (object)new[] { request!.RootLayout! });
        assert(b.TopLevel && b.Bounds == previewBounds && b.Visible && r.Bounds == rootBefore,
            "SP13 detaching commits the preview position and the source panel contraction cannot move it");

        Call(fixture.Manager, "BringTreeToFront", root.Id);
        Point insert = new(ScreenBounds(n).Left + 20, ScreenBounds(n).Top + 1);
        decision = Call(fixture.Manager, "EvaluateDrop", b, insert, Size.Empty, Keys.None)!;
        request = Property<BookmarkDropRequest?>(decision, "Request");
        assert(request is { Kind: BookmarkDropKind.InsertBefore } && request.TargetId == root.Id && request.BeforeId == nested.Id,
            "SP14 a visible insertion edge carries exact parent and sibling identity");
        decision = Call(fixture.Manager, "EvaluateDrop", a, desktop, Size.Empty, Keys.Alt)!;
        assert(!Property<bool>(decision, "Valid"),
            "SP15 Alt never creates an independently positioned child");

        fixture.Flush();
        Rectangle savedRoot = r.Bounds;
        var rootLayout = fixture.Saved[root.Id];
        fixture.FailWrites = true;
        bool moved = Pump((Task<bool>)Call(fixture.Manager, "CommitLayoutsAsync", (object)new[] { rootLayout with { Left = rootLayout.Left + 50 } })!);
        assert(!moved && r.Bounds == savedRoot,
            "SP16 failed root placement storage leaves the displayed and saved anchor unchanged");
        fixture.FailWrites = false;
        moved = Pump((Task<bool>)Call(fixture.Manager, "CommitLayoutsAsync", (object)new[] { rootLayout with { Left = rootLayout.Left + 50 } })!);
        assert(moved && r.Left > savedRoot.Left && p.Bounds.Location == peerUnderGap,
            "SP17 a successful root placement moves its own panel and does not move another sticker");
        Call(fixture.Manager, "ApplySavedLayouts", (object)new[] { rootLayout });
        assert(r.Bounds == savedRoot && a.Parent is not null,
            "SP18 applying an undo layout restores the root and derives child positions again");
        fixture.Flush();
        assert(fixture.Saved.Values.All(layout => layout.BookmarkId != first.Id && layout.BookmarkId != nested.Id && layout.BookmarkId != deep.Id),
            "SP19 derived child geometry is never written as an independent placement");

        fixture.Publish(nested with { FolderExpanded = true });
        Point subtreeDesktop = new(area.Right - 12, area.Top + area.Height / 2);
        decision = Call(fixture.Manager, "EvaluateDrop", n, subtreeDesktop, new Size(8, 8), Keys.None)!;
        request = Property<BookmarkDropRequest?>(decision, "Request");
        var panelPreview = Property<Rectangle?>(decision, "PanelBounds");
        assert(request is { Kind: BookmarkDropKind.Detach } && panelPreview is { Height: > 0 },
            "SP20 detaching an expanded subtree previews its attached descendant panel");
        fixture.Publish(nested with { ParentId = null, FolderExpanded = true });
        Call(fixture.Manager, "ApplySavedLayouts", (object)new[] { request!.RootLayout! });
        assert(n.Bounds == Property<Rectangle>(decision, "Bounds") && fixture.Panel(nested.Id).Bounds == panelPreview && d.Visible,
            "SP21 detached subtree commits both header and panel preview geometry");
        Pump((Task)Call(fixture.Manager, "HideTreeAsync", nested.Id)!);
        fixture.Publish(nested with { ParentId = null, FolderExpanded = true, DeletedAtUtc = DateTimeOffset.UtcNow });
        fixture.Publish(nested with { ParentId = null, FolderExpanded = true });
        assert(!fixture.View(nested.Id).Visible && !d.Visible && r.Visible,
            "SP22 restoring a deleted hidden root preserves session hiding for the complete tree");
        CheckDestinationDpi(fixture, r, a, assert);
        CheckScrollbarCoverage(assert);
    }

    private static void CheckTransparentCoverage(Fixture fixture, Form root, Form first, Form source, Form peer,
        Point gap, Action<bool, string> assert)
    {
        Guid rootId = Property<Bookmark>(root, "Bookmark").Id;
        Guid peerId = Property<Bookmark>(peer, "Bookmark").Id;
        object Preview(Point point) => Call(fixture.Manager, "EvaluateDrop", source, point, Size.Empty, Keys.None)!;
        BookmarkDropRequest? Request(Point point) => Property<BookmarkDropRequest?>(Preview(point), "Request");
        void Cover(Point point)
        {
            peer.Location = new Point(point.X - peer.Width / 2, point.Y - peer.Height / 2);
            Call(fixture.Manager, "ShowTree", peerId);
            Call(fixture.Manager, "BringTreeToFront", rootId);
        }

        Pump((Task<bool>)Call(fixture.Manager, "HideTreeAsync", peerId)!);
        assert(!peer.Visible && Request(gap) is { Kind: BookmarkDropKind.Detach },
            "SP26 a transparent gap with no underlying card is a desktop detach target");

        Point corner = ScreenBounds(first).Location;
        Cover(corner);
        assert(Request(corner) is { Kind: BookmarkDropKind.Group, TargetId: var cornerTarget } && cornerTarget == peerId,
            "SP27 a clipped rounded corner targets the visible card underneath");
        Pump((Task<bool>)Call(fixture.Manager, "HideTreeAsync", peerId)!);
        assert(!peer.Visible && Request(corner) is { Kind: BookmarkDropKind.Detach },
            "SP28 a clipped rounded corner over empty desktop permits detach");

        Rectangle face = ScreenBounds(first);
        Point center = new(face.Left + face.Width / 2, face.Top + face.Height / 2);
        Cover(center);
        assert(Request(center) is { Kind: BookmarkDropKind.Group, TargetId: var faceTarget } &&
            faceTarget == Property<Bookmark>(first, "Bookmark").Id,
            "SP29 a visible foreground child still covers and blocks the lower card");
        Call(fixture.Manager, "BringTreeToFront", peerId);
        assert(Request(center) is { Kind: BookmarkDropKind.Group, TargetId: var raisedTarget } && raisedTarget == peerId,
            "SP30 overlapping visible cards follow native z-order after raising another tree");

        Rectangle sourceBounds = ScreenBounds(source);
        Point sourceCenter = new(sourceBounds.Left + sourceBounds.Width / 2, sourceBounds.Top + sourceBounds.Height / 2);
        Cover(sourceCenter);
        object self = Preview(sourceCenter);
        assert(!Property<bool>(self, "Valid") && Property<BookmarkDropRequest?>(self, "Request") is null,
            "SP31 the dragged source remains excluded and its visible original row does not target a covered peer");
    }

    private static void CheckScrollbarCoverage(Action<bool, string> assert)
    {
        var rootItem = Folder("스크롤 영역", 100) with { FolderExpanded = true };
        var targetItem = Folder("스크롤바 뒤 폴더", 1) with { ParentId = rootItem.Id, SiblingOrder = 0 };
        int count = Screen.AllScreens.Max(screen => screen.WorkingArea.Height) / 36 + 8;
        var children = Enumerable.Range(1, count).Select(index => Item($"스크롤 자료 {index}", index + 1) with
            { ParentId = rootItem.Id, SiblingOrder = index }).ToArray();
        using var fixture = new Fixture([rootItem, targetItem, .. children]);
        Form root = fixture.View(rootItem.Id), target = fixture.View(targetItem.Id), source = fixture.View(children[^1].Id);
        Rectangle area = Screen.FromControl(root).WorkingArea;
        root.Location = new Point(area.Left + 40, area.Top + 40);
        var viewport = (Panel)target.Parent!;
        var panel = fixture.Panel(rootItem.Id);
        Point ScrollbarPoint() => viewport.PointToScreen(new Point(viewport.ClientSize.Width + 1, target.Top + target.Height / 2));
        object Preview() => Call(fixture.Manager, "EvaluateDrop", source, ScrollbarPoint(), Size.Empty, Keys.None)!;

        object decision = Preview();
        assert(viewport.VerticalScroll.Visible && (bool)Call(panel, "ContainsVisiblePoint", ScrollbarPoint())! &&
            !Property<bool>(decision, "Valid") && Property<BookmarkDropRequest?>(decision, "Request") is null,
            "SP32 the visible scrollbar blocks desktop detach without selecting a row");

        // Exercise the native clipping boundary independently of normal row width:
        // a resize can briefly leave a child extending behind the scrollbar.
        target.Width = viewport.Width + 12;
        viewport.AutoScrollPosition = Point.Empty;
        decision = Preview();
        assert(target.Region!.IsVisible(target.PointToClient(ScrollbarPoint())) &&
            (bool)Call(panel, "ContainsVisiblePoint", ScrollbarPoint())! &&
            !Property<bool>(decision, "Valid") && Property<BookmarkDropRequest?>(decision, "Request") is null,
            "SP33 a row extending behind a native scrollbar cannot become a drop target outside the viewport client");
    }

    private static void CheckDestinationDpi(Fixture fixture, Form root, Form child, Action<bool, string> assert)
    {
        object? StaticCall(string name, params object?[] args) => ManagerType.GetMethod(name,
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
        Size minimum96 = (Size)StaticCall("RootMinimumSizeAtDpi", 96)!;
        Size minimum120 = (Size)StaticCall("RootMinimumSizeAtDpi", 120)!;
        int logicalWidth = ((Size)ManagerType.GetProperty("DefaultExpandedSize96",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Width;
        var screen = Screen.FromControl(root);
        Rectangle area = screen.WorkingArea, original = root.Bounds;
        Size originalMinimum = root.MinimumSize, originalMaximum = root.MaximumSize;
        try
        {
            root.MaximumSize = Size.Empty;
            root.MinimumSize = minimum96;
            var scaled = new Rectangle(area.Left + 40, area.Top + 40,
                (int)Math.Round(logicalWidth * 1.25), (int)Math.Round(minimum96.Height * 1.25));
            var preview = Call(fixture.Manager, "RootMovePreview", root, scaled, screen, 120, "DPI check", false)!;
            assert((Size)StaticCall("RootMinimumSizeAtDpi", root.DeviceDpi)! == originalMinimum &&
                Property<Rectangle>(preview, "Bounds").Height == minimum120.Height,
                "SP23 120-DPI root preview uses the native fixed header height instead of scaling its old frame");

            root.MinimumSize = minimum120;
            preview = Call(fixture.Manager, "RootMovePreview", root,
                new Rectangle(area.Left + 40, area.Top + 40, logicalWidth, minimum96.Height), screen, 96, "DPI check", false)!;
            assert(Property<Rectangle>(preview, "Bounds").Size == new Size(logicalWidth, minimum96.Height),
                "SP24 moving back to 96 DPI preserves logical root width despite the source's larger minimum");

            int destinationDpi = (int)StaticCall("MonitorDpi", screen, root.DeviceDpi)!;
            root.MinimumSize = (Size)StaticCall("RootMinimumSizeAtDpi", destinationDpi * 2)!;
            preview = Call(fixture.Manager, "EvaluateDrop", child,
                new Point(area.Right - 12, area.Top + 12), new Size(8, 8), Keys.None)!;
            var request = Property<BookmarkDropRequest?>(preview, "Request");
            var bounds = Property<Rectangle>(preview, "Bounds");
            assert(request is { Kind: BookmarkDropKind.Detach, RootLayout: not null } &&
                bounds.Height == ((Size)StaticCall("RootMinimumSizeAtDpi", destinationDpi)!).Height &&
                bounds == (Rectangle)StaticCall("RestoreRootBounds", request.RootLayout, area, destinationDpi)!,
                "SP25 child detach uses destination DPI and restores the exact preview without inheriting source minimum height");
        }
        finally
        {
            root.MaximumSize = Size.Empty;
            root.MinimumSize = originalMinimum;
            root.MaximumSize = originalMaximum;
            root.Bounds = original;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Dictionary<Guid, Bookmark> _items;
        internal object Manager { get; }
        internal ConcurrentDictionary<Guid, StickerLayout> Saved { get; } = new();
        internal bool FailWrites { get; set; }
        internal Fixture(params Bookmark[] items)
        {
            _items = items.ToDictionary(item => item.Id);
            Func<Task<(IReadOnlyList<Bookmark>, IReadOnlyList<StickerLayout>)>> load = () =>
                Task.FromResult(((IReadOnlyList<Bookmark>)_items.Values.ToArray(), (IReadOnlyList<StickerLayout>)Saved.Values.ToArray()));
            Action<IReadOnlyList<StickerLayout>> save = layouts =>
            {
                if (FailWrites) throw new IOException("isolated placement failure");
                foreach (var layout in layouts) Saved[layout.BookmarkId] = layout;
            };
            Manager = ManagerType.GetConstructors(Private).Single().Invoke([load, save, (Action<string>)(_ => { })]);
            Pump((Task)Call(Manager, "SetEnabledAsync", true, false)!);
        }
        internal Form View(Guid id) => ((System.Collections.IEnumerable)ManagerType.GetProperty("Forms", Private)!.GetValue(Manager)!)
            .Cast<Form>().Single(form => Property<Bookmark>(form, "Bookmark").Id == id);
        internal Form Panel(Guid rootId) => ((System.Collections.IEnumerable)ManagerType.GetProperty("Panels", Private)!.GetValue(Manager)!)
            .Cast<Form>().Single(panel => Property<Bookmark>(Property<Form>(panel, "Root"), "Bookmark").Id == rootId);
        internal void Publish(Bookmark item) { _items[item.Id] = item; Call(Manager, "ApplyFolderChanges", new[] { item }, null); }
        internal void Flush() => Pump((Task)Call(Manager, "SavePendingAsync")!);
        public void Dispose() => ((IDisposable)Manager).Dispose();
    }
    private static Bookmark Item(string title, long sequence) => new(Guid.NewGuid(), new(TargetKind.File, @"C:\panel-policy\example.txt"),
        "example.txt", title, title, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, sequence, null, null, null, null);
    private static Bookmark Folder(string title, long sequence) => Item(title, sequence) with { Target = new(TargetKind.BookmarkFolder, "") };
    private static Rectangle ScreenBounds(Form form) => Property<Rectangle>(form, "ScreenBounds");
    private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name, Private)!.GetValue(value)!;
    private static object? Call(object value, string name, params object?[] args) => value.GetType().GetMethod(name, Private)!.Invoke(value, args);
    private static void Pump(Task task)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(15)) { Application.DoEvents(); Thread.Yield(); }
        if (!task.IsCompleted) throw new TimeoutException("Panel policy task timed out.");
        task.GetAwaiter().GetResult();
    }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
}
