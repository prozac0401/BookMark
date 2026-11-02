using System.Drawing;

namespace WorkBookmark.App;

/// <summary>Temporarily makes room for an expanded tree without changing a peer tree's geometry.</summary>
internal static class StickerTreeAvoidance
{
    /// <summary>
    /// Returns one translation per peer group. Empty means unchanged; null means no free placement
    /// was found. The expanded tree's complete bounding box remains reserved, including its gaps.
    /// </summary>
    internal static Point?[] FindOffsets(IReadOnlyList<Rectangle> expandedTree,
        IReadOnlyList<Rectangle[]> groups, Rectangle workingArea, int dpi,
        IReadOnlyList<Rectangle>? protectedAreas = null)
    {
        ArgumentNullException.ThrowIfNull(expandedTree);
        ArgumentNullException.ThrowIfNull(groups);
        if (workingArea.Width <= 0 || workingArea.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(workingArea));

        var offsets = Enumerable.Repeat<Point?>(Point.Empty, groups.Count).ToArray();
        Rectangle? reserved = Envelope(expandedTree);
        if (reserved is null) return offsets;

        int gap = Math.Max(1, (int)Math.Round(8 * Math.Max(96, dpi) / 96d));
        var fixedAreas = new List<Rectangle> { reserved.Value };
        if (protectedAreas is not null)
            fixedAreas.AddRange(protectedAreas.Where(area => area.Width > 0 && area.Height > 0));
        var original = groups.Select(group => Envelope(group)).ToArray();
        var current = (Rectangle?[])original.Clone();
        for (int index = 0; index < groups.Count; index++)
            if (original[index] is { } bounds && fixedAreas.Any(area => Overlaps(bounds, area, gap)))
                offsets[index] = null;

        // Reserve unchanged groups as well as groups already displaced. A group can move farther
        // past a neighbour, but cannot create a new overlap that would require that neighbour to move.
        // Retry failures when another affected group has vacated a useful position.
        bool progress;
        do
        {
            progress = false;
            for (int index = 0; index < groups.Count; index++)
            {
                if (offsets[index] is not null || original[index] is not { } bounds) continue;
                var obstacles = new List<Rectangle>(fixedAreas);
                for (int peer = 0; peer < current.Length; peer++)
                    if (peer != index && current[peer] is { } obstacle) obstacles.Add(obstacle);

                Point? translation = FindOffset(bounds, obstacles, workingArea, gap);
                if (translation is not { } offset) continue;
                offsets[index] = offset;
                bounds.Offset(offset);
                current[index] = bounds;
                progress = true;
            }
        } while (progress && offsets.Any(offset => offset is null));

        if (offsets.Any(offset => offset is null))
        {
            // Some affected groups occupy one another's only escape route. Plan their moves
            // together without reserving those old positions, and apply the plan only if every
            // remaining group fits. This bounded pass does not disturb unrelated groups or leave
            // a half-applied plan colliding with a group that could not move.
            int[] remaining = Enumerable.Range(0, groups.Count).Where(index => offsets[index] is null)
                .OrderByDescending(index => (long)original[index]!.Value.Width * original[index]!.Value.Height)
                .ThenBy(index => index).ToArray();
            var obstacles = new List<Rectangle>(fixedAreas);
            for (int index = 0; index < current.Length; index++)
                if (offsets[index] is not null && current[index] is { } bounds) obstacles.Add(bounds);
            var plan = new List<(int Index, Point Offset)>();
            foreach (int index in remaining)
            {
                Rectangle bounds = original[index]!.Value;
                if (FindOffset(bounds, obstacles, workingArea, gap) is not { } offset) break;
                plan.Add((index, offset));
                bounds.Offset(offset);
                obstacles.Add(bounds);
            }
            if (plan.Count == remaining.Length)
                foreach (var (index, offset) in plan) offsets[index] = offset;
        }

        return offsets;
    }

    private static Point? FindOffset(Rectangle group, IReadOnlyList<Rectangle> obstacles,
        Rectangle area, int gap)
    {
        if (group.Width > area.Width || group.Height > area.Height) return null;
        int minX = area.Left - group.Left, maxX = area.Right - group.Right;
        int minY = area.Top - group.Top, maxY = area.Bottom - group.Bottom;
        var xs = new HashSet<int> { Math.Clamp(0, minX, maxX), minX, maxX };
        var ys = new HashSet<int> { Math.Clamp(0, minY, maxY), minY, maxY };
        foreach (Rectangle obstacle in obstacles)
        {
            Add(xs, (long)obstacle.Right + gap - group.Left, minX, maxX);
            Add(xs, (long)obstacle.Left - gap - group.Right, minX, maxX);
            Add(ys, (long)obstacle.Bottom + gap - group.Top, minY, maxY);
            Add(ys, (long)obstacle.Top - gap - group.Bottom, minY, maxY);
        }

        // A nearest legal placement lies on an obstacle edge or a screen edge on each moved axis.
        // Sorting and pruning keeps the usual case small while still finding diagonal openings.
        int[] orderedX = xs.OrderBy(value => Math.Abs((long)value)).ThenBy(value => value < 0).ToArray();
        int[] orderedY = ys.OrderBy(value => Math.Abs((long)value)).ThenBy(value => value < 0).ToArray();
        Point? best = null;
        long bestDistance = long.MaxValue;
        foreach (int x in orderedX)
        {
            if (Math.Abs((long)x) > bestDistance) break;
            foreach (int y in orderedY)
            {
                long distance = Math.Abs((long)x) + Math.Abs((long)y);
                if (distance > bestDistance) break;
                var candidate = new Rectangle(group.X + x, group.Y + y, group.Width, group.Height);
                if (obstacles.Any(obstacle => Overlaps(candidate, obstacle, gap))) continue;
                // On equal distance, retain the first candidate: prefer vertical motion, then
                // downward/rightward motion. The ordering never depends on hash-set iteration.
                if (distance >= bestDistance) continue;
                best = new Point(x, y);
                bestDistance = distance;
            }
        }
        return best;

        static void Add(HashSet<int> candidates, long value, int minimum, int maximum)
        {
            if (value >= minimum && value <= maximum) candidates.Add((int)value);
        }
    }

    private static Rectangle? Envelope(IReadOnlyList<Rectangle> rectangles)
    {
        Rectangle? result = null;
        foreach (Rectangle rectangle in rectangles)
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0) continue;
            result = result is { } bounds ? Rectangle.Union(bounds, rectangle) : rectangle;
        }
        return result;
    }

    private static bool Overlaps(Rectangle first, Rectangle second, int gap) =>
        (long)first.Left < (long)second.Right + gap && (long)first.Right + gap > second.Left &&
        (long)first.Top < (long)second.Bottom + gap && (long)first.Bottom + gap > second.Top;
}
