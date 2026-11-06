using System.Drawing;

namespace WorkBookmark.App;

internal enum StickerTreeOpeningSide { Below, Above }

internal sealed record StickerTreeViewportPlan(Rectangle Bounds, StickerTreeOpeningSide Side, int ContentHeight);

/// <summary>Measures visible rows and attaches one bounded viewport to a fixed root.</summary>
internal static class StickerTreeLayout
{
    internal static int MeasureContentHeight(IEnumerable<int> rowHeights, int dpi)
    {
        int gap = Scale(8, dpi);
        long height = 0;
        int count = 0;
        foreach (int rowHeight in rowHeights)
        {
            if (count++ > 0) height += gap;
            height += Math.Max(0, rowHeight);
        }
        return (int)Math.Min(int.MaxValue, height);
    }

    /// <summary>Plans a bounded child viewport without changing the root anchor.</summary>
    internal static StickerTreeViewportPlan? PlanViewport(Rectangle rootBounds, Rectangle workingArea,
        int contentHeight, int dpi, StickerTreeOpeningSide? preferredSide = null)
    {
        if (contentHeight <= 0 || rootBounds.Width <= 0 || workingArea.Width <= 0 || workingArea.Height <= 0 ||
            rootBounds.Left < workingArea.Left || rootBounds.Right > workingArea.Right) return null;
        int border = Scale(1, dpi);
        int minimum = Scale(36, dpi) + border * 2;
        int below = Math.Max(0, workingArea.Bottom - rootBounds.Bottom);
        int above = Math.Max(0, rootBounds.Top - workingArea.Top);
        StickerTreeOpeningSide side;
        if (preferredSide is { } chosen) side = chosen;
        else if (below >= minimum) side = StickerTreeOpeningSide.Below;
        else if (above >= minimum) side = StickerTreeOpeningSide.Above;
        else return null;
        int available = side == StickerTreeOpeningSide.Below ? below : above;
        if (available < minimum) return null;
        int height = (int)Math.Min(available, (long)contentHeight + border * 2);
        int top = side == StickerTreeOpeningSide.Below ? rootBounds.Bottom : rootBounds.Top - height;
        var bounds = new Rectangle(rootBounds.Left, top, rootBounds.Width, height);
        return workingArea.Contains(bounds) ? new(bounds, side, contentHeight) : null;
    }

    private static int Scale(int value, int dpi) => Math.Max(1, (int)Math.Round(value * Math.Max(96, dpi) / 96d));

}
