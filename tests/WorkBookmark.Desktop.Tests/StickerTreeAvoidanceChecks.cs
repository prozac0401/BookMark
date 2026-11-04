using System.Drawing;
using System.Reflection;
using WorkBookmark.App;

namespace WorkBookmark.Desktop.Tests;

internal static class StickerTreeAvoidanceChecks
{
    private static readonly MethodInfo FindOffsetsMethod = typeof(BookmarkApplicationContext).Assembly
        .GetType("WorkBookmark.App.StickerTreeAvoidance")!
        .GetMethod("FindOffsets", BindingFlags.Static | BindingFlags.NonPublic)!;

    internal static void Run(Action<bool, string> assert)
    {
        var area = new Rectangle(0, 0, 1000, 700);
        Rectangle[] tree = [new(100, 100, 260, 36), new(120, 144, 260, 36), new(120, 188, 260, 36)];
        Rectangle[][] adjacent = [[new(120, 200, 260, 36)], [new(650, 100, 260, 36)]];
        var offsets = FindOffsets(tree, adjacent, area);
        assert(offsets.SequenceEqual(new Point?[] { new(0, 32), Point.Empty }),
            "TA01 expansion moves an overlapping root the shortest distance and leaves distant roots unchanged");
        assert(FindOffsets([], adjacent, area).All(offset => offset == Point.Empty) &&
            FindOffsets(tree, [], area).Length == 0,
            "TA02 empty trees and peer collections do not require temporary displacement");

        Rectangle[][] branch = [[new(120, 200, 260, 36), new(140, 244, 280, 36)]];
        var branchOffset = FindOffsets(tree, branch, area).Single();
        assert(branchOffset == new Point(0, 32) && Clear(tree, branch, [branchOffset], area, 8),
            "TA03 a neighbouring root and its descendants share one translation and retain their spacing");

        Rectangle[] sparseTree = [new(100, 100, 260, 36), new(120, 240, 260, 36)];
        Rectangle[][] inGap = [[new(120, 160, 260, 36)]];
        var gapOffsets = FindOffsets(sparseTree, inGap, area);
        assert(gapOffsets[0] != Point.Empty && Clear(sparseTree, inGap, gapOffsets, area, 8),
            "TA04 the full expanded tree envelope is reserved even where individual stickers leave empty space");

        Rectangle[][] chain = [[new(120, 200, 260, 36)], [new(120, 244, 260, 36)], [new(120, 288, 260, 36)]];
        var chainOffsets = FindOffsets(tree, chain, area);
        assert(chainOffsets[0] == new Point(0, 132) && chainOffsets[1] == Point.Empty &&
            chainOffsets[2] == Point.Empty && Clear(tree, chain, chainOffsets, area, 8),
            "TA05 displacement clears neighbouring roots without introducing a chain of overlaps");

        Rectangle[] edgeTree = [new(720, 500, 260, 36), new(740, 544, 260, 36), new(740, 588, 260, 36)];
        Rectangle[][] edgeGroups = [[new(740, 610, 260, 80)]];
        var edgeOffsets = FindOffsets(edgeTree, edgeGroups, area);
        assert(edgeOffsets[0] == new Point(0, -198) && Clear(edgeTree, edgeGroups, edgeOffsets, area, 8),
            "TA06 expansion near a screen edge moves a peer toward available space and keeps it fully on screen");

        var negativeArea = new Rectangle(-1440, -320, area.Width, area.Height);
        var negativeTree = tree.Select(rectangle => Move(rectangle, negativeArea.Location)).ToArray();
        var negativeGroups = adjacent.Select(group => group.Select(rectangle => Move(rectangle, negativeArea.Location)).ToArray()).ToArray();
        assert(FindOffsets(negativeTree, negativeGroups, negativeArea).SequenceEqual(offsets),
            "TA07 temporary avoidance is independent of negative monitor origins");
        var doubleTree = tree.Select(Double).ToArray();
        var doubleGroups = adjacent.Select(group => group.Select(Double).ToArray()).ToArray();
        assert(FindOffsets(doubleTree, doubleGroups, Double(area), 192)
            .SequenceEqual(offsets.Select(offset => offset is { } point ? new Point?(new Point(point.X * 2, point.Y * 2)) : null)),
            "TA08 temporary avoidance scales its clearance with monitor DPI");

        Rectangle[] fullTree = [new(0, 0, 300, 200)];
        Rectangle[][] crowded = [[new(30, 40, 100, 36)]];
        assert(FindOffsets(fullTree, crowded, new Rectangle(0, 0, 300, 200)).Single() is null,
            "TA09 an impossible placement is reported without returning an overlapping or off-screen position");
        Rectangle[][] oversized = [[new(0, 0, 1100, 100)]];
        assert(FindOffsets(tree, oversized, area).Single() is null,
            "TA10 an oversized peer group is reported without resizing or splitting its tree");

        Rectangle[][] multiple = [[new(120, 200, 260, 36)], [new(120, 150, 260, 36)], [new(650, 100, 260, 36)]];
        var multipleOffsets = FindOffsets(tree, multiple, area);
        assert(multipleOffsets.All(offset => offset is not null) && Clear(tree, multiple, multipleOffsets, area, 8) &&
            multipleOffsets.SequenceEqual(FindOffsets(tree, multiple, area)),
            "TA11 multiple affected roots receive deterministic, mutually disjoint placements");

        Rectangle[] protectedAreas = [new(100, 232, 280, 124)];
        var protectedOffsets = FindOffsets(tree, adjacent, area, protectedAreas: protectedAreas);
        assert(protectedOffsets[0] == new Point(0, -144) &&
            !Move(adjacent[0][0], protectedOffsets[0]!.Value).IntersectsWith(protectedAreas[0]),
            "TA12 displacement respects an earlier expanded tree's separate fixed reservation");
        Rectangle[] separatedAreas = [new(100, 500, 280, 124)];
        var betweenOffsets = FindOffsets(tree, adjacent, area, protectedAreas: separatedAreas);
        assert(betweenOffsets.SequenceEqual(offsets),
            "TA13 separate protected tree boxes retain the free space between their reservations");

        Rectangle[] blockedTree = [new(90, 50, 140, 80)];
        Rectangle[][] mutuallyBlocked = [[new(70, 10, 120, 70)], [new(60, 130, 70, 40)], [new(220, 110, 70, 90)]];
        var tightArea = new Rectangle(0, 0, 300, 220);
        var coordinatedOffsets = FindOffsets(blockedTree, mutuallyBlocked, tightArea);
        assert(coordinatedOffsets.All(offset => offset is not null) &&
            Clear(blockedTree, mutuallyBlocked, coordinatedOffsets, tightArea, 8),
            "TA14 mutually blocked affected roots move together when a complete coordinated placement fits");
        Rectangle[][] impossibleTogether = [.. mutuallyBlocked, [new(0, 0, 60, 100)]];
        var failedPlan = FindOffsets(blockedTree, impossibleTogether, tightArea);
        assert(failedPlan[0] is null && failedPlan[2] is null && failedPlan[3] == Point.Empty,
            "TA15 an incomplete coordinated plan is discarded without moving an unrelated root or applying half the plan");

        var neighboringMonitor = new Rectangle(300, 0, 300, 220);
        Rectangle[] crossingReservation = [new(250, 20, 100, 120)];
        Rectangle[][] neighboringPeers = [[new(320, 70, 100, 36)]];
        var crossingOffsets = FindOffsets(crossingReservation, neighboringPeers, neighboringMonitor);
        assert(crossingOffsets[0] == new Point(38, 0) &&
            Clear(crossingReservation, neighboringPeers, crossingOffsets, neighboringMonitor, 8),
            "TA16 a reservation crossing from an adjacent monitor moves an overlapping peer within the peer's own working area");
        Rectangle[] distantReservation = [new(0, 20, 100, 120)];
        assert(FindOffsets(distantReservation, neighboringPeers, neighboringMonitor)[0] == Point.Empty,
            "TA17 a distant monitor's reservation leaves an unrelated peer at its existing position");

        Rectangle[] centralTree = [new(400, 250, 200, 200)];
        (Rectangle Peer, Point Expected)[] directional =
        [
            (new(450, 235, 80, 36), new(0, -29)),
            (new(450, 435, 80, 36), new(0, 23)),
            (new(370, 310, 80, 36), new(-58, 0)),
            (new(570, 310, 80, 36), new(38, 0))
        ];
        foreach (var (peer, expected) in directional)
        {
            Rectangle[][] directionalGroup = [[peer]];
            Point? offset = FindOffsets(centralTree, directionalGroup, area).Single();
            Rectangle[][] negativeDirectionalGroup = [[Move(peer, negativeArea.Location)]];
            Point? negativeOffset = FindOffsets(centralTree.Select(rectangle => Move(rectangle, negativeArea.Location)).ToArray(),
                negativeDirectionalGroup, negativeArea).Single();
            assert(offset == expected && negativeOffset == expected && Clear(centralTree, directionalGroup, [offset], area, 8),
                $"TA18 nearest collision escape follows direction {expected} without changing sign on a negative-origin monitor");
        }
    }

    private static Point?[] FindOffsets(IReadOnlyList<Rectangle> tree, IReadOnlyList<Rectangle[]> groups,
        Rectangle area, int dpi = 96, IReadOnlyList<Rectangle>? protectedAreas = null) =>
        (Point?[])FindOffsetsMethod.Invoke(null, [tree, groups, area, dpi, protectedAreas])!;

    private static bool Clear(IReadOnlyList<Rectangle> tree, IReadOnlyList<Rectangle[]> groups,
        IReadOnlyList<Point?> offsets, Rectangle area, int gap)
    {
        var occupied = new List<Rectangle> { tree.Aggregate(Rectangle.Union) };
        for (int index = 0; index < groups.Count; index++)
        {
            if (offsets[index] is not { } offset) return false;
            Rectangle bounds = Move(groups[index].Aggregate(Rectangle.Union), offset);
            if (!area.Contains(bounds)) return false;
            if (occupied.Any(other => bounds.Left < other.Right + gap && bounds.Right + gap > other.Left &&
                bounds.Top < other.Bottom + gap && bounds.Bottom + gap > other.Top)) return false;
            occupied.Add(bounds);
        }
        return true;
    }

    private static Rectangle Move(Rectangle rectangle, Point offset)
    {
        rectangle.Offset(offset);
        return rectangle;
    }

    private static Rectangle Double(Rectangle rectangle) =>
        new(rectangle.X * 2, rectangle.Y * 2, rectangle.Width * 2, rectangle.Height * 2);
}
