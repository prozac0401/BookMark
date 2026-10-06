using System.Drawing;
using System.Reflection;
using WorkBookmark.App;

namespace WorkBookmark.Desktop.Tests;

internal static class StickerArrangementChecks
{
    private static readonly Type ArrangementType = typeof(BookmarkApplicationContext).Assembly
        .GetType("WorkBookmark.App.StickerArrangement")!;
    private static readonly Type ModeType = typeof(BookmarkApplicationContext).Assembly
        .GetType("WorkBookmark.App.StickerArrangementMode")!;

    internal static void Run(Action<bool, string> assert)
    {
        string[] modes = ["Grid", "Horizontal", "Vertical"];
        var area = new Rectangle(0, 0, 400, 300);
        Size[] sizes = [new(100, 90), new(140, 40), new(80, 60), new(120, 70)];
        assert(modes.All(mode => Arrange([], area, 96, mode).Count == 0),
            "SA01 all arrangement modes accept an empty sticker collection");

        var horizontal = Arrange(sizes, area, 96, "Horizontal");
        assert(horizontal.Select(rectangle => rectangle.Location).SequenceEqual(
            new Point[] { new(16, 16), new(124, 16), new(272, 16), new(16, 114) }),
            "SA02 horizontal layout packs left-to-right and wraps below the tallest sticker in the row");
        var transposed = sizes.Select(size => new Size(size.Height, size.Width)).ToArray();
        var vertical = Arrange(transposed, new Rectangle(0, 0, 300, 400), 96, "Vertical");
        assert(vertical.Select(rectangle => rectangle.Location).SequenceEqual(
            new Point[] { new(16, 16), new(16, 124), new(16, 272), new(114, 16) }),
            "SA03 vertical layout packs top-to-bottom and wraps beside the widest sticker in the column");

        var grid = Arrange(sizes, area, 96, "Grid");
        assert(grid.Select(rectangle => rectangle.Location).SequenceEqual(
            new Point[] { new(16, 16), new(164, 16), new(16, 114), new(164, 114) }),
            "SA04 grid aligns unequal and collapsed sticker sizes to a consistent row and column pitch");
        assert(horizontal.Select(rectangle => rectangle.Size).SequenceEqual(sizes) &&
            vertical.Select(rectangle => rectangle.Size).SequenceEqual(transposed) &&
            grid.Select(rectangle => rectangle.Size).SequenceEqual(sizes),
            "SA05 every arrangement retains input order and each sticker's exact size");
        assert(Disjoint(horizontal) && Disjoint(vertical) && Disjoint(grid) &&
            horizontal.All(area.Contains) && grid.All(area.Contains),
            "SA06 mixed sticker sizes do not overlap when they fit on the first page");

        foreach (string mode in modes)
        {
            var original = Arrange(sizes, area, 96, mode);
            var negativeArea = new Rectangle(-1440, -320, area.Width, area.Height);
            var negative = Arrange(sizes, negativeArea, 96, mode);
            assert(negative.Select(rectangle => rectangle.Location).SequenceEqual(original.Select(rectangle =>
                new Point(rectangle.X + negativeArea.X, rectangle.Y + negativeArea.Y))),
                $"SA07 {mode} respects negative monitor coordinates");
            var doubleScale = Arrange(sizes.Select(size => new Size(size.Width * 2, size.Height * 2)).ToArray(),
                new Rectangle(0, 0, 800, 600), 192, mode);
            assert(doubleScale.SequenceEqual(original.Select(rectangle =>
                new Rectangle(rectangle.X * 2, rectangle.Y * 2, rectangle.Width * 2, rectangle.Height * 2))),
                $"SA08 {mode} scales margins and gaps with monitor DPI");

            var smallArea = new Rectangle(-400, 80, 260, 170);
            var crowdedSizes = Enumerable.Repeat(new Size(100, 50), 37).ToArray();
            var crowded = Arrange(crowdedSizes, smallArea, 96, mode);
            assert(crowded.All(smallArea.Contains) && crowded.Select(rectangle => rectangle.Size).SequenceEqual(crowdedSizes) &&
                crowded.SequenceEqual(Arrange(crowdedSizes, smallArea, 96, mode)),
                $"SA09 {mode} overflow placement stays on-screen, preserves sizes and is deterministic");
            assert(crowded[4].Location != crowded[0].Location,
                $"SA10 {mode} starts its overflow page with a visible cascade offset");

            Size[] oversized = [new(500, 350)];
            var large = Arrange(oversized, area, 96, mode).Single();
            assert(large.Size == oversized[0] && large.Location == area.Location,
                $"SA11 {mode} keeps an oversized sticker's top-left reachable without resizing it");
        }

        Size[] mixedHeights = [new(100, 100), new(100, 100), new(100, 20), new(100, 60)];
        var mixed = Arrange(mixedHeights, new Rectangle(0, 0, 248, 180), 96, "Horizontal");
        assert(mixed[2].Top == mixed[3].Top && mixed[2].Top == 36 && mixed[3].Bottom <= 180,
            "SA12 a row containing collapsed and expanded stickers moves together to the next page when needed");
        var tinyArea = new Rectangle(-10, -10, 20, 20);
        assert(modes.All(mode => tinyArea.Contains(Arrange([new Size(10, 10)], tinyArea, 96, mode).Single())),
            "SA13 small working areas reduce margins and retain a reachable sticker");
    }

    private static IReadOnlyList<Rectangle> Arrange(IReadOnlyList<Size> sizes, Rectangle area, int dpi, string mode) =>
        (IReadOnlyList<Rectangle>)ArrangementType.GetMethod("Arrange", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [sizes, area, dpi, Enum.Parse(ModeType, mode)])!;

    private static bool Disjoint(IReadOnlyList<Rectangle> bounds)
    {
        for (int first = 0; first < bounds.Count; first++)
            for (int second = first + 1; second < bounds.Count; second++)
                if (bounds[first].IntersectsWith(bounds[second])) return false;
        return true;
    }
}
