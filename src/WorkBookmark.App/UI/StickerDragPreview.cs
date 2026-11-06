using System.Drawing;

namespace WorkBookmark.App.UI;

/// <summary>A nonactivating, noninteractive picture of the operation that release will commit.</summary>
internal sealed class StickerDragPreview : Form
{
    private string _label = "";
    private bool _valid;
    internal StickerDragPreview()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(220, 239, 234);
        Opacity = .88;
        DoubleBuffered = true;
        Font = new Font("맑은 고딕", 9F);
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var value = base.CreateParams; value.ExStyle |= 0x20 | 0x80 | 0x08000000; return value; }
    }
    internal void SetPreview(Rectangle bounds, string label, bool valid)
    {
        _label = label; _valid = valid;
        Bounds = new Rectangle(bounds.Location, new Size(Math.Max(150, bounds.Width), Math.Max(28, bounds.Height)));
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(_valid ? Color.Teal : Color.Firebrick, 2);
        e.Graphics.DrawRectangle(pen, 1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
        TextRenderer.DrawText(e.Graphics, _label, Font, Rectangle.Inflate(ClientRectangle, -6, -4),
            Color.FromArgb(25, 60, 55), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) Font.Dispose();
        base.Dispose(disposing);
    }
}
