using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using WorkBookmark.App;
using WorkBookmark.Core;

namespace WorkBookmark.Desktop.Tests;

internal static class StickerTreeAvoidanceIntegrationChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type ManagerType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.StickerManager")!;

    internal static void Run(Action<bool, string> assert)
    {
        ExpansionAndPersistence(assert);
        ManualMovement(assert);
        NestedExpansion(assert);
        MultipleExpansions(assert);
        OpeningDisplacedFolder(assert, restart: false);
        OpeningDisplacedFolder(assert, restart: true);
        ReopeningNestedTree(assert);
        MovingExpandedRoot(assert);
        EditingAndResizingFootprint(assert);
        GroupingDisplacedTarget(assert);
    }

    private static void ExpansionAndPersistence(Action<bool, string> assert)
    {
        var root = Folder("펼칠 프로젝트", 1);
        var child = Item("사용자가 멀리 배치한 자식", 2) with { ParentId = root.Id };
        var peer = Folder("주변 프로젝트", 3);
        var peerChild = Item("접힌 주변 프로젝트 자료", 4) with { ParentId = peer.Id };
        var distant = Item("멀리 떨어진 스티커", 5);
        using var fixture = new Fixture(root, child, peer, peerChild, distant);
        Form a = fixture.View(root.Id), ac = fixture.View(child.Id), b = fixture.View(peer.Id), bc = fixture.View(peerChild.Id), far = fixture.View(distant.Id);
        Rectangle area = Screen.FromControl(a).WorkingArea;
        int gap = Scale(a, 8), row = a.Height + gap;
        Point anchor = area.Location + new Size(Scale(a, 32), Scale(a, 32));
        fixture.Place(a, anchor);
        fixture.Place(ac, anchor + new Size(Scale(a, 23), row * 4));
        fixture.Place(b, anchor + new Size(Scale(a, 10), row * 2));
        fixture.Place(bc, b.Location + new Size(Scale(a, 20), row));
        fixture.Place(far, new Point(area.Right - far.Width - Scale(a, 32), area.Bottom - far.Height - Scale(a, 32)));
        fixture.Flush();
        Rectangle aBefore = a.Bounds, acBefore = ac.Bounds, bBefore = b.Bounds, bcBefore = bc.Bounds, farBefore = far.Bounds;
        var savedBefore = fixture.Saved.ToDictionary(pair => pair.Key, pair => pair.Value);
        assert(!b.Bounds.IntersectsWith(a.Bounds) && !b.Bounds.IntersectsWith(ac.Bounds) &&
            Rectangle.Union(a.Bounds, ac.Bounds).IntersectsWith(b.Bounds),
            "TAI01 the neighboring root fixture occupies empty space inside the custom expanded tree rectangle");

        fixture.Publish(root with { FolderExpanded = true });
        Rectangle reserved = Rectangle.Union(a.Bounds, ac.Bounds);
        var displacement = new Size(b.Left - bBefore.Left, b.Top - bBefore.Top);
        assert(a.Bounds == aBefore && ac.Bounds == acBefore && ac.Visible && b.Location != bBefore.Location &&
            !reserved.IntersectsWith(b.Bounds) && area.Contains(b.Bounds) && far.Bounds == farBefore,
            "TAI02 opening a tree clears its complete virtual rectangle while retaining custom child placement and distant stickers");
        assert(bc.Location == bcBefore.Location + displacement && bc.Size == bcBefore.Size && !bc.Visible,
            "TAI03 avoidance translates a neighboring tree as one unit, including its hidden descendants");
        fixture.Flush();
        assert(savedBefore.All(pair => fixture.Saved[pair.Key] == pair.Value),
            "TAI04 automatic avoidance does not replace saved root or descendant placements");

        Rectangle displaced = b.Bounds;
        Call(fixture.Manager, "HideAll");
        Pump((Task)Call(fixture.Manager, "ShowAllAsync", false)!);
        assert(b.Visible && ac.Visible && b.Bounds == displaced && !reserved.IntersectsWith(b.Bounds),
            "TAI05 hiding and showing an expanded tree retains a stable nonoverlapping temporary placement");

        fixture.Publish(root);
        assert(!ac.Visible && a.Bounds == aBefore && ac.Bounds == acBefore && b.Bounds == bBefore && bc.Bounds == bcBefore,
            "TAI06 closing the tree restores displaced roots and hidden descendants exactly");
        fixture.Publish(root with { FolderExpanded = true });
        fixture.Restart();
        a = fixture.View(root.Id); ac = fixture.View(child.Id); b = fixture.View(peer.Id); bc = fixture.View(peerChild.Id);
        assert(savedBefore.All(pair => fixture.Saved[pair.Key] == pair.Value) &&
            a.Bounds == aBefore && ac.Bounds == acBefore && !Rectangle.Union(a.Bounds, ac.Bounds).IntersectsWith(b.Bounds),
            "TAI07 shutdown persists original placements and restart reapplies expanded-tree avoidance");
        fixture.Publish(root);
        assert(b.Bounds == bBefore && bc.Bounds == bcBefore,
            "TAI08 closing a tree after restart restores the original neighboring tree placement");
    }

    private static void ManualMovement(Action<bool, string> assert)
    {
        var root = Folder("이동 시험 프로젝트", 1);
        var child = Item("펼칠 자료", 2) with { ParentId = root.Id };
        var peer = Folder("직접 이동할 프로젝트", 3);
        var peerChild = Item("함께 이동할 자료", 4) with { ParentId = peer.Id };
        using var fixture = new Fixture(root, child, peer, peerChild);
        Form a = fixture.View(root.Id), ac = fixture.View(child.Id), b = fixture.View(peer.Id), bc = fixture.View(peerChild.Id);
        Rectangle area = Screen.FromControl(a).WorkingArea;
        int row = a.Height + Scale(a, 8);
        Point anchor = area.Location + new Size(Scale(a, 40), Scale(a, 40));
        fixture.Place(a, anchor);
        fixture.Place(ac, anchor + new Size(Scale(a, 20), row * 4));
        fixture.Place(b, anchor + new Size(0, row * 2));
        fixture.Place(bc, b.Location + new Size(Scale(a, 20), row));
        fixture.Flush();
        Rectangle baseline = b.Bounds;
        fixture.Publish(root with { FolderExpanded = true });
        Rectangle displaced = b.Bounds, childDisplaced = bc.Bounds;
        Point chosen = new(area.Right - b.Width - Scale(b, 40), area.Bottom - row * 3);

        // Model the final location restored by Windows when Escape cancels its move loop.
        b.Bounds = (Rectangle)Call(fixture.Manager, "MoveSticker", b, new Rectangle(chosen, b.Size), Keys.Alt)!;
        b.Location = displaced.Location;
        Call(fixture.Manager, "FinishMove", b, false, Cursor.Position, Keys.Alt);
        assert(b.Bounds == displaced && bc.Bounds == childDisplaced,
            "TAI09 cancelling movement of an automatically displaced tree restores its temporary presentation");
        fixture.Publish(root);
        assert(b.Bounds == baseline,
            "TAI10 a cancelled manual drag does not discard the original avoidance restore point");

        fixture.Publish(root with { FolderExpanded = true });
        Point movingStart = b.Location, childStart = bc.Location;
        b.Bounds = (Rectangle)Call(fixture.Manager, "MoveSticker", b, new Rectangle(chosen, b.Size), Keys.Alt)!;
        Call(fixture.Manager, "FinishMove", b, true, Cursor.Position, Keys.Alt);
        var moved = new Size(chosen.X - movingStart.X, chosen.Y - movingStart.Y);
        fixture.Publish(root);
        fixture.Flush();
        assert(b.Location == chosen && bc.Location == childStart + moved && b.Bounds != baseline,
            "TAI11 directly moving a displaced root keeps the chosen location and its descendant offsets after collapse");
        Rectangle manual = b.Bounds, manualChild = bc.Bounds;
        fixture.Restart();
        assert(fixture.View(peer.Id).Bounds == manual && fixture.View(peerChild.Id).Bounds == manualChild,
            "TAI12 a manually chosen placement replaces the temporary restore point in persistent storage");
    }

    private static void NestedExpansion(Action<bool, string> assert)
    {
        var root = Folder("상위 프로젝트", 1);
        var nested = Folder("하위 프로젝트", 2) with { ParentId = root.Id };
        var deep = Item("깊은 자료", 3) with { ParentId = nested.Id };
        var peer = Item("깊은 확장 영역의 스티커", 4);
        using var fixture = new Fixture(root, nested, deep, peer);
        Form a = fixture.View(root.Id), n = fixture.View(nested.Id), d = fixture.View(deep.Id), p = fixture.View(peer.Id);
        Rectangle area = Screen.FromControl(a).WorkingArea;
        int row = a.Height + Scale(a, 8);
        Point anchor = area.Location + new Size(Scale(a, 40), Scale(a, 40));
        fixture.Place(a, anchor);
        fixture.Place(n, anchor + new Size(Scale(a, 20), row));
        fixture.Place(d, anchor + new Size(Scale(a, 40), row * 5));
        fixture.Place(p, anchor + new Size(0, row * 3));
        Rectangle original = p.Bounds;
        fixture.Publish(root with { FolderExpanded = true });
        assert(n.Visible && !d.Visible && p.Bounds == original,
            "TAI13 a closed nested folder does not reserve space for its hidden descendants");
        fixture.Publish(nested with { FolderExpanded = true });
        assert(d.Visible && !Rectangle.Union(a.Bounds, d.Bounds).IntersectsWith(p.Bounds),
            "TAI14 nested expansion extends its root tree's reservation to the newly visible descendant");
        fixture.Publish(nested);
        assert(!d.Visible && p.Bounds == original,
            "TAI15 collapsing a nested folder restores peers while the root remains expanded");
        fixture.Publish(nested with { FolderExpanded = true });
        fixture.Publish(root);
        assert(!n.Visible && !d.Visible && p.Bounds == original,
            "TAI16 closing an ancestor releases reservations from expanded but hidden nested folders");
        fixture.Publish(root with { FolderExpanded = true });
        assert(d.Visible && !Rectangle.Union(a.Bounds, d.Bounds).IntersectsWith(p.Bounds),
            "TAI17 reopening an ancestor reapplies its preserved nested expansion without accumulating offsets");
    }

    private static void MultipleExpansions(Action<bool, string> assert)
    {
        var first = Folder("첫 번째 프로젝트", 1);
        var firstChild = Item("첫 번째 하위 자료", 2) with { ParentId = first.Id };
        var second = Folder("두 번째 프로젝트", 3);
        var secondChild = Item("두 번째 하위 자료", 4) with { ParentId = second.Id };
        var peer = Item("두 펼침 영역에 걸친 스티커", 5);
        using var fixture = new Fixture(first, firstChild, second, secondChild, peer);
        Form a = fixture.View(first.Id), ac = fixture.View(firstChild.Id), b = fixture.View(second.Id), bc = fixture.View(secondChild.Id), p = fixture.View(peer.Id);
        Rectangle area = Screen.FromControl(a).WorkingArea;
        int row = a.Height + Scale(a, 8);
        Point anchor = area.Location + new Size(Scale(a, 32), Scale(a, 32));
        fixture.Place(a, anchor);
        fixture.Place(ac, anchor + new Size(0, row * 4));
        fixture.Place(b, anchor + new Size(a.Width + Scale(a, 48), 0));
        fixture.Place(bc, b.Location + new Size(0, row * 5));
        fixture.Place(p, anchor + new Size(a.Width - Scale(a, 48), row * 2));
        Rectangle baseline = p.Bounds;
        fixture.Publish(first with { FolderExpanded = true });
        fixture.Publish(second with { FolderExpanded = true });
        assert(!Rectangle.Union(a.Bounds, ac.Bounds).IntersectsWith(p.Bounds) &&
            !Rectangle.Union(b.Bounds, bc.Bounds).IntersectsWith(p.Bounds),
            "TAI18 a second expanded root retains all active tree reservations when moving a shared neighbor");
        fixture.Publish(first);
        assert(!Rectangle.Union(b.Bounds, bc.Bounds).IntersectsWith(p.Bounds),
            "TAI19 closing one root cannot restore a neighbor into another expanded tree's reserved rectangle");
        fixture.Publish(second);
        assert(p.Bounds == baseline,
            "TAI20 closing all expanded roots restores the shared neighbor without cumulative displacement");
    }

    private static void OpeningDisplacedFolder(Action<bool, string> assert, bool restart)
    {
        var first = Folder("먼저 펼친 프로젝트", 1);
        var firstChild = Item("첫 프로젝트의 아래쪽 자료", 2) with { ParentId = first.Id };
        var second = Folder("밀려난 위치에서 펼칠 프로젝트", 3);
        var secondChild = Item("두 번째 프로젝트 자료", 4) with { ParentId = second.Id };
        using var fixture = new Fixture(first, firstChild, second, secondChild);
        Form a = fixture.View(first.Id), ac = fixture.View(firstChild.Id), b = fixture.View(second.Id), bc = fixture.View(secondChild.Id);
        Rectangle area = Screen.FromControl(a).WorkingArea;
        int row = a.Height + Scale(a, 8);
        Point anchor = area.Location + new Size(Scale(a, 40), Scale(a, 40));
        fixture.Place(a, anchor);
        fixture.Place(ac, anchor + new Size(Scale(a, 20), row * 4));
        fixture.Place(b, anchor + new Size(0, row * 2));
        fixture.Place(bc, b.Location + new Size(Scale(a, 20), row));
        fixture.Flush();
        Rectangle aBefore = a.Bounds, acBefore = ac.Bounds, bBefore = b.Bounds, bcBefore = bc.Bounds;
        var savedBefore = fixture.Saved.ToDictionary(pair => pair.Key, pair => pair.Value);

        fixture.Publish(first with { FolderExpanded = true });
        Rectangle bDisplaced = b.Bounds, bcDisplaced = bc.Bounds;
        if (restart)
        {
            fixture.Restart();
            a = fixture.View(first.Id); ac = fixture.View(firstChild.Id);
            b = fixture.View(second.Id); bc = fixture.View(secondChild.Id);
        }
        assert(bDisplaced != bBefore && b.Top > ac.Bottom && !bc.Visible,
            $"TAI35 restart={restart}: opening the first folder displaces the closed neighboring folder below its expanded tree");
        fixture.Publish(second with { FolderExpanded = true });
        assert(a.Bounds == aBefore && ac.Bounds == acBefore && b.Bounds == bDisplaced && bc.Bounds == bcDisplaced && bc.Visible &&
            !Rectangle.Union(a.Bounds, ac.Bounds).IntersectsWith(Rectangle.Union(b.Bounds, bc.Bounds)),
            $"TAI36 restart={restart}: opening a displaced folder retains its displayed anchor and does not push the already expanded tree away");
        for (int repeat = 0; repeat < 3; repeat++)
        {
            fixture.Publish(second);
            fixture.Publish(second with { FolderExpanded = true });
            Call(fixture.Manager, "UpdateTreeAvoidance");
        }
        fixture.Flush();
        assert(a.Bounds == aBefore && ac.Bounds == acBefore && b.Bounds == bDisplaced && bc.Bounds == bcDisplaced &&
            savedBefore.All(pair => fixture.Saved[pair.Key] == pair.Value),
            $"TAI37 restart={restart}: repeated neighboring-folder expansion and recalculation do not reverse directions, accumulate offsets, or overwrite saved placements");
        fixture.Publish(second);
        fixture.Publish(first);
        assert(a.Bounds == aBefore && ac.Bounds == acBefore && b.Bounds == bBefore && bc.Bounds == bcBefore,
            $"TAI38 restart={restart}: closing both neighboring folders restores every root and descendant to its original coordinates");
    }

    private static void ReopeningNestedTree(Action<bool, string> assert)
    {
        var first = Folder("접었다 다시 펼칠 상위 프로젝트", 1);
        var nested = Folder("열림 상태를 보존할 하위 프로젝트", 2) with { ParentId = first.Id };
        var deep = Item("하위 프로젝트 자료", 3) with { ParentId = nested.Id };
        var second = Folder("상위 프로젝트를 접은 동안 펼칠 프로젝트", 4);
        var secondChild = Item("먼저 자리를 확보한 자료", 5) with { ParentId = second.Id };
        using var fixture = new Fixture(first, nested, deep, second, secondChild);
        Form a = fixture.View(first.Id), n = fixture.View(nested.Id), d = fixture.View(deep.Id),
            b = fixture.View(second.Id), bc = fixture.View(secondChild.Id);
        Rectangle area = Screen.FromControl(a).WorkingArea;
        int row = a.Height + Scale(a, 8);
        Point anchor = area.Location + new Size(Scale(a, 40), Scale(a, 40));
        fixture.Place(a, anchor);
        fixture.Place(n, anchor + new Size(Scale(a, 20), row));
        fixture.Place(d, anchor + new Size(Scale(a, 40), row * 4));
        fixture.Place(b, anchor + new Size(0, row * 2));
        fixture.Place(bc, b.Location + new Size(Scale(a, 20), row));
        fixture.Flush();
        Rectangle aBefore = a.Bounds, nBefore = n.Bounds, dBefore = d.Bounds, bBefore = b.Bounds, bcBefore = bc.Bounds;
        var savedBefore = fixture.Saved.ToDictionary(pair => pair.Key, pair => pair.Value);

        fixture.Publish(first with { FolderExpanded = true });
        fixture.Publish(nested with { FolderExpanded = true });
        fixture.Publish(first);
        fixture.Publish(second with { FolderExpanded = true });
        assert(!n.Visible && !d.Visible && b.Bounds == bBefore && bc.Bounds == bcBefore && bc.Visible,
            "TAI39 hiding an expanded nested tree releases its old reservation before another folder is opened");
        fixture.Publish(first with { FolderExpanded = true });
        var delta = new Size(a.Left - aBefore.Left, a.Top - aBefore.Top);
        assert(n.Visible && d.Visible && b.Bounds == bBefore && bc.Bounds == bcBefore &&
            n.Location == nBefore.Location + delta && d.Location == dBefore.Location + delta &&
            !Rectangle.Union(a.Bounds, d.Bounds).IntersectsWith(Rectangle.Union(b.Bounds, bc.Bounds)),
            "TAI40 reopening an ancestor cannot use its hidden nested folder's old priority to displace the already open neighboring tree");
        Rectangle reopened = a.Bounds;
        fixture.Publish(first);
        fixture.Publish(first with { FolderExpanded = true });
        fixture.Flush();
        assert(a.Bounds == reopened && b.Bounds == bBefore && bc.Bounds == bcBefore &&
            savedBefore.All(pair => fixture.Saved[pair.Key] == pair.Value),
            "TAI41 repeated ancestor reopening preserves the visible tree's priority and all saved coordinates");
        fixture.Publish(first);
        fixture.Publish(second);
        assert(a.Bounds == aBefore && n.Bounds == nBefore && d.Bounds == dBefore && b.Bounds == bBefore && bc.Bounds == bcBefore,
            "TAI42 closing the neighboring and nested trees restores every original coordinate");
    }

    private static void MovingExpandedRoot(Action<bool, string> assert)
    {
        var root = Folder("확장한 채 이동할 프로젝트", 1);
        var child = Item("함께 이동할 자식", 2) with { ParentId = root.Id };
        var peer = Item("이동 후 복원할 스티커", 3);
        using var fixture = new Fixture(root, child, peer);
        Form a = fixture.View(root.Id), ac = fixture.View(child.Id), p = fixture.View(peer.Id);
        Rectangle area = Screen.FromControl(a).WorkingArea;
        int row = a.Height + Scale(a, 8);
        Point anchor = area.Location + new Size(Scale(a, 40), Scale(a, 40));
        fixture.Place(a, anchor);
        fixture.Place(ac, anchor + new Size(Scale(a, 20), row * 4));
        fixture.Place(p, anchor + new Size(0, row * 2));
        Rectangle baseline = p.Bounds;
        fixture.Publish(root with { FolderExpanded = true });
        assert(p.Bounds != baseline,
            "TAI21 expanding the movable active tree initially displaces its neighbor");

        Point destination = new(area.Right - a.Width - Scale(a, 60), anchor.Y);
        a.Bounds = (Rectangle)Call(fixture.Manager, "MoveSticker", a, new Rectangle(destination, a.Size), Keys.Alt)!;
        Call(fixture.Manager, "FinishMove", a, true, Cursor.Position, Keys.Alt);
        assert(a.Location == destination && ac.Visible && p.Bounds == baseline &&
            !Rectangle.Union(a.Bounds, ac.Bounds).IntersectsWith(p.Bounds),
            "TAI22 moving an expanded active tree away immediately restores its displaced neighbor without requiring collapse");

        a.Bounds = (Rectangle)Call(fixture.Manager, "MoveSticker", a, new Rectangle(anchor, a.Size), Keys.Alt)!;
        Call(fixture.Manager, "FinishMove", a, true, Cursor.Position, Keys.Alt);
        assert(p.Bounds != baseline && !Rectangle.Union(a.Bounds, ac.Bounds).IntersectsWith(p.Bounds),
            "TAI23 returning an expanded tree reapplies its reservation at the new final drag position");
        fixture.Publish(root);
        assert(p.Bounds == baseline,
            "TAI24 moving an expanded root twice does not accumulate a neighboring sticker's restore offset");
    }

    private static void EditingAndResizingFootprint(Action<bool, string> assert)
    {
        var root = Folder("편집 영역 시험 프로젝트", 1);
        var child = Item("메모를 편집할 자식", 2) with { ParentId = root.Id };
        var peer = Item("편집기 아래의 스티커", 3);
        using var fixture = new Fixture(root, child, peer);
        Form a = fixture.View(root.Id), ac = fixture.View(child.Id), p = fixture.View(peer.Id);
        Rectangle area = Screen.FromControl(a).WorkingArea;
        int gap = Scale(a, 8), row = a.Height + gap;
        Point anchor = area.Location + new Size(Scale(a, 40), Scale(a, 40));
        fixture.Place(a, anchor);
        fixture.Place(ac, anchor + new Size(Scale(a, 20), row));
        fixture.Place(p, new Point(ac.Left, ac.Bottom + gap * 2));
        fixture.Publish(root with { FolderExpanded = true });
        fixture.Flush();
        Rectangle childBefore = ac.Bounds, peerBefore = p.Bounds;
        var savedBefore = fixture.Saved.ToDictionary(pair => pair.Key, pair => pair.Value);
        int saves = 0;
        Func<string, Task> save = _ => { saves++; return Task.CompletedTask; };

        bool editing = (bool)Call(fixture.Manager, "EditNote", child, save)!;
        assert(editing && ac.Height > childBefore.Height && p.Bounds != peerBefore &&
            !Rectangle.Union(a.Bounds, ac.Bounds).IntersectsWith(p.Bounds),
            "TAI25 opening an inline editor extends an expanded tree's reservation and clears the newly occupied space");
        Call(ac, "CancelNoteEdit");
        fixture.Flush();
        assert(saves == 0 && ac.Bounds == childBefore && p.Bounds == peerBefore &&
            savedBefore.All(pair => fixture.Saved[pair.Key] == pair.Value),
            "TAI26 cancelling inline editing restores the neighboring sticker and preserves both saved placements");

        Call(fixture.Manager, "EditNote", child, save);
        ((TextBox)ac.GetType().GetField("_noteEditor", Private)!.GetValue(ac)!).Text = "저장 후에도 원위치";
        assert(p.Bounds != peerBefore,
            "TAI27 reopening the editor reapplies its temporary footprint without losing the original peer position");
        Pump((Task)Call(ac, "SaveNoteAsync")!);
        fixture.Flush();
        assert(saves == 1 && ac.Bounds == childBefore && p.Bounds == peerBefore &&
            savedBefore.All(pair => fixture.Saved[pair.Key] == pair.Value),
            "TAI28 saving an inline note releases its temporary expanded footprint and restores the peer exactly");

        ac.Height += Scale(ac, 60);
        Call(ac, "OnResizeEnd", EventArgs.Empty);
        assert(p.Bounds != peerBefore && !Rectangle.Union(a.Bounds, ac.Bounds).IntersectsWith(p.Bounds),
            "TAI29 completing a child resize updates the expanded root reservation");
        ac.Size = childBefore.Size;
        Call(ac, "OnResizeEnd", EventArgs.Empty);
        assert(p.Bounds == peerBefore,
            "TAI30 shrinking the child releases the extra reserved area and restores its displaced neighbor");
    }

    private static void GroupingDisplacedTarget(Action<bool, string> assert)
    {
        var root = Folder("주변 영역을 차지한 프로젝트", 1);
        var child = Item("펼칠 자식", 2) with { ParentId = root.Id };
        var target = Item("임시 이동된 묶기 대상", 3);
        var source = Item("묶기에 추가할 스티커", 4);
        using var fixture = new Fixture(root, child, target, source);
        Form a = fixture.View(root.Id), ac = fixture.View(child.Id), b = fixture.View(target.Id), c = fixture.View(source.Id);
        Rectangle area = Screen.FromControl(a).WorkingArea;
        int row = a.Height + Scale(a, 8);
        Point anchor = area.Location + new Size(Scale(a, 40), Scale(a, 40));
        fixture.Place(a, anchor);
        fixture.Place(ac, anchor + new Size(Scale(a, 20), row * 4));
        b.Width += Scale(b, 16);
        fixture.Place(b, anchor + new Size(0, row * 2));
        fixture.Place(c, new Point(area.Right - c.Width - Scale(c, 40), area.Top + Scale(c, 40)));
        Rectangle targetOriginal = b.Bounds;
        fixture.Publish(root with { FolderExpanded = true });
        Rectangle displayedTarget = b.Bounds;
        assert(displayedTarget != targetOriginal,
            "TAI31 the grouping target is temporarily displaced before an explicit grouping operation");

        var grouped = Folder("표시된 위치에 만든 폴더", 5);
        fixture.Group([grouped, target with { ParentId = grouped.Id }, source with { ParentId = grouped.Id }], target.Id);
        Form folder = fixture.View(grouped.Id);
        assert(folder.Visible && !b.Visible && !c.Visible && folder.Bounds == displayedTarget,
            "TAI32 grouping onto a displaced target anchors the new closed folder at the target's displayed location and size");
        fixture.Publish(root);
        assert(folder.Bounds == displayedTarget && folder.Bounds != targetOriginal,
            "TAI33 closing the surrounding tree retains the deliberately created folder's displayed target location");
        fixture.Restart();
        assert(fixture.View(grouped.Id).Bounds == displayedTarget && !fixture.View(target.Id).Visible && !fixture.View(source.Id).Visible,
            "TAI34 grouping adopts the displayed target placement persistently without reopening the new folder on restart");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Dictionary<Guid, Bookmark> _items;
        internal ConcurrentDictionary<Guid, StickerLayout> Saved { get; } = new();
        internal object Manager { get; private set; }

        internal Fixture(params Bookmark[] items)
        {
            _items = items.ToDictionary(item => item.Id);
            Manager = Create();
            Pump((Task)Call(Manager, "SetEnabledAsync", true, false)!);
        }

        private object Create()
        {
            Func<Task<(IReadOnlyList<Bookmark> Items, IReadOnlyList<StickerLayout> Layouts)>> load = () =>
                Task.FromResult(((IReadOnlyList<Bookmark>)_items.Values.ToArray(), (IReadOnlyList<StickerLayout>)Saved.Values.ToArray()));
            Action<IReadOnlyList<StickerLayout>> save = batch =>
            {
                foreach (var layout in batch) Saved[layout.BookmarkId] = layout;
            };
            return ManagerType.GetConstructors(Private).Single().Invoke([load, save,
                (Action<string>)(message => throw new InvalidOperationException(message))]);
        }

        internal Form View(Guid id) => ((System.Collections.IEnumerable)ManagerType.GetProperty("Forms", Private)!.GetValue(Manager)!)
            .Cast<Form>().Single(form => ((Bookmark)form.GetType().GetProperty("Bookmark")!.GetValue(form)!).Id == id);

        internal void Publish(Bookmark item)
        {
            _items[item.Id] = item;
            Call(Manager, "ApplyFolderChanges", new[] { item }, null);
        }

        internal void Group(Bookmark[] changes, Guid targetId)
        {
            foreach (var item in changes) _items[item.Id] = item;
            Call(Manager, "ApplyFolderChanges", changes, targetId);
        }

        internal void Place(Form form, Point location)
        {
            form.Location = location;
            Call(form, "OnResizeEnd", EventArgs.Empty);
        }

        internal void Flush()
        {
            PumpUntil(() => ManagerType.GetField("_writeTask", Private)!.GetValue(Manager) is null);
            Pump((Task)Call(Manager, "SavePendingAsync")!);
        }

        internal void Restart()
        {
            ((IDisposable)Manager).Dispose();
            Manager = Create();
            Pump((Task)Call(Manager, "SetEnabledAsync", true, false)!);
        }

        public void Dispose() => ((IDisposable)Manager).Dispose();
    }

    private static int Scale(Form form, int value) => Math.Max(1, (int)Math.Round(value * form.DeviceDpi / 96d));
    private static Bookmark Item(string title, long sequence) => new(Guid.NewGuid(), new CapturedTarget(TargetKind.File, @"C:\tree-avoidance\sample.txt"),
        "sample.txt", title, title, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, sequence, null, null, null, null);
    private static Bookmark Folder(string title, long sequence) => Item(title, sequence) with { Target = new(TargetKind.BookmarkFolder, "") };
    private static object? Call(object value, string name, params object?[] arguments) =>
        value.GetType().GetMethod(name, Private | BindingFlags.Public)!.Invoke(value, arguments);
    private static void Pump(Task task)
    {
        PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }
    private static void PumpUntil(Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready() && timer.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Yield(); }
        if (!ready()) throw new TimeoutException("Tree avoidance integration check timed out.");
    }
}
