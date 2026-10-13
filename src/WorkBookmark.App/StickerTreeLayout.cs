using System.Drawing;

namespace WorkBookmark.App;

/// <summary>Parent-first rows with a small indentation; overflow continues in reachable columns.</summary>
internal static class StickerTreeLayout
{
    internal static IReadOnlyList<Rectangle> Arrange(IReadOnlyList<(Size Size, int Depth)> rows,
        Point anchor, Rectangle workingArea, int dpi)
    {
        if (rows.Count == 0) return [];
        int Scale(int value) => Math.Max(1, (int)Math.Round(value * Math.Max(96, dpi) / 96d));
        int indent = Scale(20), gap = Scale(8);
        int width = rows.Max(row => row.Size.Width + row.Depth * indent);
        int height = rows.Sum(row => row.Size.Height) + (rows.Count - 1) * gap;
        int column = Math.Clamp(anchor.X, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - width));
        int top = Math.Clamp(anchor.Y, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - height));
        int right = column, page = 0;
        var result = new Rectangle[rows.Count];
        for (int index = 0; index < rows.Count; index++)
        {
            var (size, depth) = rows[index];
            if (index > 0 && top + size.Height > workingArea.Bottom)
            {
                column = right + gap;
                top = workingArea.Top;
                if (column + width > workingArea.Right)
                {
                    // As with the existing arrangement, excess pages cascade so
                    // every individual sticker retains a reachable title/drag area.
                    page++;
                    column = workingArea.Left + (page % 6) * indent;
                    top += (page % 6) * indent;
                }
                right = column;
            }
            int left = Math.Clamp(column + depth * indent, workingArea.Left,
                Math.Max(workingArea.Left, workingArea.Right - size.Width));
            int y = Math.Clamp(top, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - size.Height));
            result[index] = new Rectangle(new Point(left, y), size);
            right = Math.Max(right, left + size.Width);
            top = y + size.Height + gap;
        }
        return result;
    }
}
