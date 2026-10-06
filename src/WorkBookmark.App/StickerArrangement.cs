using System.Drawing;

namespace WorkBookmark.App;

internal enum StickerArrangementMode { Grid, Horizontal, Vertical }

internal static class StickerArrangement
{
    internal static IReadOnlyList<Rectangle> Arrange(IReadOnlyList<Size> sizes, Rectangle workingArea,
        int dpi, StickerArrangementMode mode)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (sizes.Count == 0) return [];
        if (workingArea.Width <= 0 || workingArea.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(workingArea));
        if (sizes.Any(size => size.Width <= 0 || size.Height <= 0))
            throw new ArgumentException("Sticker sizes must be positive.", nameof(sizes));

        int Scale(int value) => (int)Math.Round(value * Math.Max(96, dpi) / 96D);
        int gap = Scale(8), margin = Scale(16), cascade = Scale(20);
        int insetX = Math.Min(margin, (workingArea.Width - 1) / 2);
        int insetY = Math.Min(margin, (workingArea.Height - 1) / 2);
        var content = new Rectangle(workingArea.Left + insetX, workingArea.Top + insetY,
            workingArea.Width - 2 * insetX, workingArea.Height - 2 * insetY);
        var result = new Rectangle[sizes.Count];

        void Place(int index, int x, int y, int page)
        {
            // Extra pages remain reachable as a short repeating cascade, just like
            // initial sticker placement. Clamp position only: arranging never resizes.
            int offset = page % 6 * cascade;
            Size size = sizes[index];
            int left = Math.Clamp(content.Left + x + offset, workingArea.Left,
                Math.Max(workingArea.Left, workingArea.Right - size.Width));
            int top = Math.Clamp(content.Top + y + offset, workingArea.Top,
                Math.Max(workingArea.Top, workingArea.Bottom - size.Height));
            result[index] = new Rectangle(new Point(left, top), size);
        }

        if (mode == StickerArrangementMode.Grid)
        {
            int width = sizes.Max(size => size.Width), height = sizes.Max(size => size.Height);
            int columns = Math.Max(1, (content.Width + gap) / (width + gap));
            int rows = Math.Max(1, (content.Height + gap) / (height + gap));
            int capacity = columns * rows;
            for (int index = 0; index < sizes.Count; index++)
            {
                int slot = index % capacity;
                Place(index, slot % columns * (width + gap), slot / columns * (height + gap), index / capacity);
            }
            return result;
        }

        bool vertical = mode == StickerArrangementMode.Vertical;
        int MainSize(Size size) => vertical ? size.Height : size.Width;
        int CrossSize(Size size) => vertical ? size.Width : size.Height;
        int mainLimit = vertical ? content.Height : content.Width;
        int crossLimit = vertical ? content.Width : content.Height;
        int start = 0, cross = 0, pageIndex = 0;
        while (start < sizes.Count)
        {
            // Measure a complete row/column before placing it. In particular, a
            // tall sticker after a collapsed one must not overflow the current page.
            int end = start, main = 0, extent = 0;
            while (end < sizes.Count)
            {
                int next = (end == start ? 0 : main + gap) + MainSize(sizes[end]);
                if (end > start && next > mainLimit) break;
                main = next;
                extent = Math.Max(extent, CrossSize(sizes[end]));
                end++;
            }
            if (cross > 0 && cross + extent > crossLimit)
            {
                cross = 0;
                pageIndex++;
            }
            main = 0;
            for (int index = start; index < end; index++)
            {
                Place(index, vertical ? cross : main, vertical ? main : cross, pageIndex);
                main += MainSize(sizes[index]) + gap;
            }
            cross += extent + gap;
            start = end;
        }
        return result;
    }
}
