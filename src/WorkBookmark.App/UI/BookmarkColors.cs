using System.Drawing;
using System.Windows.Forms;
using WorkBookmark.Core;

namespace WorkBookmark.App.UI;

internal static class BookmarkColors
{
    internal sealed record Palette(Color Paper, Color Hover, Color Rule, Color Swatch);

    internal static Palette Get(BookmarkColor color) => color switch
    {
        BookmarkColor.Yellow => Make(255, 247, 211, 191, 145, 35),
        BookmarkColor.Green => Make(231, 245, 232, 70, 137, 91),
        BookmarkColor.Blue => Make(229, 241, 252, 67, 126, 174),
        BookmarkColor.Pink => Make(252, 233, 239, 174, 90, 125),
        BookmarkColor.Purple => Make(241, 234, 251, 129, 103, 176),
        BookmarkColor.Gray => Make(236, 240, 243, 111, 125, 137),
        _ => new(Color.FromArgb(255, 253, 245), Color.FromArgb(241, 239, 228), Color.FromArgb(226, 225, 213), Color.FromArgb(182, 183, 172))
    };

    internal static Color ListPaper(BookmarkColor color) => color == BookmarkColor.Default ? UiStyle.Surface : Get(color).Paper;

    internal static ToolStripMenuItem CreateMenu(Func<Bookmark?> selected, Action<Bookmark, BookmarkColor> request, Func<bool>? enabled = null)
    {
        var menu = new ToolStripMenuItem("색상");
        (BookmarkColor Value, string Name)[] choices =
        [
            (BookmarkColor.Default, "기본"), (BookmarkColor.Yellow, "노랑"), (BookmarkColor.Green, "초록"),
            (BookmarkColor.Blue, "파랑"), (BookmarkColor.Pink, "분홍"), (BookmarkColor.Purple, "보라"), (BookmarkColor.Gray, "회색")
        ];
        foreach (var choice in choices)
        {
            var swatch = new Bitmap(16, 16);
            using (var graphics = Graphics.FromImage(swatch))
            using (var paper = new SolidBrush(Get(choice.Value).Paper))
            using (var border = new Pen(Get(choice.Value).Swatch))
            {
                graphics.FillRectangle(paper, 1, 1, 13, 13);
                graphics.DrawRectangle(border, 1, 1, 13, 13);
            }
            var item = new ToolStripMenuItem(choice.Name, swatch) { Tag = choice.Value, CheckOnClick = false };
            item.Click += (_, _) =>
            {
                if ((enabled?.Invoke() ?? true) && selected() is { } bookmark && bookmark.Color != choice.Value)
                    request(bookmark, choice.Value);
            };
            item.Disposed += (_, _) => swatch.Dispose();
            menu.DropDownItems.Add(item);
        }
        menu.DropDownOpening += (_, _) => UpdateMenu(menu, selected(), enabled?.Invoke() ?? true);
        return menu;
    }

    internal static void UpdateMenu(ToolStripMenuItem menu, Bookmark? selected, bool enabled = true)
    {
        menu.Enabled = selected is not null && enabled;
        foreach (ToolStripMenuItem item in menu.DropDownItems)
            item.Checked = selected is not null && item.Tag is BookmarkColor color && selected.Color == color;
    }

    private static Palette Make(int red, int green, int blue, int swatchRed, int swatchGreen, int swatchBlue) =>
        new(Color.FromArgb(red, green, blue), Color.FromArgb(red - 10, green - 10, blue - 10),
            Color.FromArgb(red - 24, green - 24, blue - 24), Color.FromArgb(swatchRed, swatchGreen, swatchBlue));
}
