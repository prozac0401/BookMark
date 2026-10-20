using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace WorkBookmark.App.UI;

/// <summary>A native keyboard/accessibility button with smoothly painted action artwork.</summary>
internal sealed class StickerActionButton : Button
{
    private bool _hovered, _pointerPressed, _keyboardPressed, _nativePressed;
    private Bitmap? _artwork;
    private ArtworkKey? _artworkKey;

    internal StickerActionButton() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
        ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Busy { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool HasIssue { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool IsFolder { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool FolderExpanded { get; set; }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        var key = new ArtworkKey(ClientSize, DeviceDpi, _hovered,
            (_pointerPressed && _hovered) || _keyboardPressed || _nativePressed,
            Focused && ShowFocusCues, Enabled, Busy, IsFolder, FolderExpanded,
            Parent?.BackColor ?? SystemColors.Control, BackColor, ForeColor,
            FlatAppearance.MouseOverBackColor, FlatAppearance.MouseDownBackColor);
        if (_artworkKey != key)
        {
            _artwork?.Dispose();
            _artwork = Render(key);
            _artworkKey = key;
        }
        e.Graphics.DrawImageUnscaled(_artwork!, Point.Empty);
    }

    private static Bitmap Render(ArtworkKey key)
    {
        // Composite the entire button at 4x device resolution and downsample once.
        // A native child-window Region would cut away the antialiased corner pixels.
        const int samples = 4;
        using var large = new Bitmap(key.Size.Width * samples, key.Size.Height * samples, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(large))
        {
            graphics.Clear(key.Paper);
            graphics.ScaleTransform(samples, samples);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float scale = key.Dpi / 96F;
            Color background = key.Pressed ? key.PressedPaper : key.Hovered ? key.HoverPaper : key.Background;
            Color foreground = key.Foreground;
            if (!key.Enabled && !key.Busy)
            {
                background = Blend(background, key.Paper, .55F);
                foreground = Blend(foreground, key.Paper, .45F);
            }
            using var plate = Rounded(new RectangleF(0, 0, key.Size.Width, key.Size.Height), 8 * scale);
            using (var brush = new SolidBrush(background)) graphics.FillPath(brush, plate);

            float offset = key.Pressed ? .5F * scale : 0;
            var icon = new RectangleF((key.Size.Width - 20 * scale) / 2 + offset,
                (key.Size.Height - 20 * scale) / 2 + offset, 20 * scale, 20 * scale);
            if (key.Busy)
            {
                using var pen = new Pen(foreground, 1.8F * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                var progress = RectangleF.Inflate(icon, -2 * scale, -2 * scale);
                using var track = new Pen(Color.FromArgb(65, foreground), pen.Width);
                graphics.DrawEllipse(track, progress);
                graphics.DrawArc(pen, progress, -90, 255);
            }
            else if (key.IsFolder)
            {
                var folder = new RectangleF(icon.X - scale, icon.Y - scale, icon.Width, icon.Height);
                StickerPictograms.DrawFolder(graphics, folder, foreground, key.FolderExpanded);
                // The caret communicates reveal/hide; a plus/minus would suggest add/remove.
                using (var brush = new SolidBrush(background))
                    graphics.FillEllipse(brush, icon.X + 11 * scale, icon.Y + 10 * scale, 12 * scale, 12 * scale);
                PointF At(float x, float y) => new(icon.X + x * scale, icon.Y + y * scale);
                using var caret = new Pen(foreground, 1.7F * scale)
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                graphics.DrawLines(caret, key.FolderExpanded
                    ? [At(13.5F, 14.5F), At(16.5F, 17.5F), At(19.5F, 14.5F)]
                    : [At(15, 12.5F), At(18, 15.5F), At(15, 18.5F)]);
            }
            else StickerPictograms.DrawOpen(graphics, icon, foreground);

            if (key.Focus)
            {
                using var focus = Rounded(new RectangleF(2 * scale, 2 * scale,
                    key.Size.Width - 4 * scale, key.Size.Height - 4 * scale), 6 * scale);
                using var pen = new Pen(Color.FromArgb(185, foreground), 1.1F * scale);
                graphics.DrawPath(pen, focus);
            }
        }
        var result = new Bitmap(key.Size.Width, key.Size.Height, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(result))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(large, new Rectangle(Point.Empty, key.Size), 0, 0, large.Width, large.Height,
                GraphicsUnit.Pixel, attributes);
        }
        return result;
    }

    private static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color Blend(Color first, Color second, float amount) => Color.FromArgb(
        (int)Math.Round(first.R * (1 - amount) + second.R * amount),
        (int)Math.Round(first.G * (1 - amount) + second.G * amount),
        (int)Math.Round(first.B * (1 - amount) + second.B * amount));

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; base.OnMouseLeave(e); Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _pointerPressed = true; _hovered = true; }
        base.OnMouseDown(e); Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) _pointerPressed = false;
        base.OnMouseUp(e); Invalidate();
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (!Capture) _pointerPressed = false;
        base.OnMouseCaptureChanged(e); Invalidate();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && !e.Alt) _keyboardPressed = true;
        base.OnKeyDown(e); Invalidate();
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) _keyboardPressed = false;
        base.OnKeyUp(e); Invalidate();
    }
    protected override void OnLostFocus(EventArgs e)
    {
        _keyboardPressed = false;
        base.OnLostFocus(e); Invalidate();
    }
    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled) _pointerPressed = _keyboardPressed = _nativePressed = false;
        base.OnEnabledChanged(e); Invalidate();
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x00F3) { _nativePressed = message.WParam != nint.Zero; Invalidate(); } // BM_SETSTATE
        base.WndProc(ref message);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _artwork?.Dispose();
        base.Dispose(disposing);
    }

    private readonly record struct ArtworkKey(Size Size, int Dpi, bool Hovered, bool Pressed, bool Focus,
        bool Enabled, bool Busy, bool IsFolder, bool FolderExpanded, Color Paper, Color Background,
        Color Foreground, Color HoverPaper, Color PressedPaper);
}

internal sealed class StickerHierarchyMark : Label
{
    protected override void OnPaint(PaintEventArgs e)
    {
        if (int.TryParse(Text.TrimEnd('+'), out _))
        {
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, UiStyle.Muted, BackColor,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96F, middle = Height / 2F;
        using var pen = new Pen(Color.FromArgb(145, UiStyle.Muted), 1.3F * scale)
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (Text == "↳")
        {
            using var branch = new GraphicsPath();
            branch.AddLine(5 * scale, middle - 5 * scale, 5 * scale, middle);
            branch.AddBezier(5 * scale, middle, 5 * scale, middle + 3 * scale,
                7 * scale, middle + 3 * scale, 10 * scale, middle + 3 * scale);
            branch.AddLine(10 * scale, middle + 3 * scale, 13 * scale, middle + 3 * scale);
            e.Graphics.DrawPath(pen, branch);
        }
        else
        {
            e.Graphics.DrawLine(pen, 5 * scale, middle - 4 * scale, 13 * scale, middle - 4 * scale);
            e.Graphics.DrawLine(pen, 9 * scale, middle - 4 * scale, 9 * scale, middle + 5 * scale);
        }
    }
}
