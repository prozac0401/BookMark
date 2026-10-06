using System.Drawing;

namespace WorkBookmark.App;

/// <summary>Magnetic placement of outer window bounds; callers supply visible peers only.</summary>
internal static class StickerSnap
{
    internal static Rectangle Snap(Rectangle proposed, IReadOnlyList<Rectangle> neighbors,
        Rectangle workingArea, int dpi)
    {
        if (proposed.Width <= 0 || proposed.Height <= 0 || neighbors.Count == 0) return proposed;
        double scale = (dpi > 0 ? dpi : 96) / 96d;
        int gap = Math.Max(1, (int)Math.Round(8 * scale));
        int threshold = Math.Max(1, (int)Math.Round(12 * scale));

        // Evaluate Y after X, so snapping a corner never introduces an overlap
        // that neither independent axis correction would have noticed.
        var result = SnapAxis(proposed, neighbors, workingArea, gap, threshold, horizontal: true);
        return SnapAxis(result, neighbors, workingArea, gap, threshold, horizontal: false);
    }

    private static Rectangle SnapAxis(Rectangle proposed, IReadOnlyList<Rectangle> neighbors,
        Rectangle workingArea, int gap, int threshold, bool horizontal)
    {
        int origin = horizontal ? proposed.Left : proposed.Top;
        int length = horizontal ? proposed.Width : proposed.Height;
        int start = horizontal ? workingArea.Left : workingArea.Top;
        int end = horizontal ? workingArea.Right : workingArea.Bottom;
        int best = origin;
        long bestDistance = (long)threshold + 1;
        HashSet<long> considered = [];

        foreach (Rectangle peer in neighbors)
        {
            if (peer.Width <= 0 || peer.Height <= 0) continue;
            int spanStart = horizontal ? proposed.Top : proposed.Left;
            int spanEnd = horizontal ? proposed.Bottom : proposed.Right;
            int peerSpanStart = horizontal ? peer.Top : peer.Left;
            int peerSpanEnd = horizontal ? peer.Bottom : peer.Right;
            // Rows and columns must be local. Matching a distant sticker's edge
            // should not make a freely dragged sticker jump across empty space.
            long separation = Math.Max((long)spanStart - peerSpanEnd, (long)peerSpanStart - spanEnd);
            if (separation > gap + threshold) continue;

            int peerStart = horizontal ? peer.Left : peer.Top;
            int peerEnd = horizontal ? peer.Right : peer.Bottom;
            Consider(peerStart);                    // Same left / top edge.
            Consider((long)peerEnd - length);       // Same right / bottom edge.
            Consider((long)peerEnd + gap);          // After the peer.
            Consider((long)peerStart - gap - length); // Before the peer.
        }

        return horizontal
            ? new Rectangle(best, proposed.Top, proposed.Width, proposed.Height)
            : new Rectangle(proposed.Left, best, proposed.Width, proposed.Height);

        void Consider(long coordinate)
        {
            long distance = Math.Abs(coordinate - origin);
            if (distance > threshold || distance > bestDistance ||
                (distance == bestDistance && coordinate >= best) ||
                coordinate < start || coordinate + length > end || !considered.Add(coordinate)) return;

            var candidate = horizontal
                ? new Rectangle((int)coordinate, proposed.Top, proposed.Width, proposed.Height)
                : new Rectangle(proposed.Left, (int)coordinate, proposed.Width, proposed.Height);
            // Existing overlap is allowed during free movement, but magnets do
            // not pull cards further over one another or obstruct crossing them.
            if (coordinate != origin)
                foreach (Rectangle peer in neighbors)
                    if (OverlapArea(candidate, peer) > OverlapArea(proposed, peer)) return;

            best = (int)coordinate;
            bestDistance = distance;
        }
    }

    private static long OverlapArea(Rectangle first, Rectangle second)
    {
        long width = Math.Max(0L, (long)Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left));
        long height = Math.Max(0L, (long)Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top));
        return width * height;
    }
}
