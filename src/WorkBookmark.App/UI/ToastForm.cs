using System.Drawing;
using System.Windows.Forms;

namespace WorkBookmark.App.UI;

internal sealed class ToastForm : Form
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var value = base.CreateParams; value.ExStyle |= 0x08000000 | 0x00000080; return value; } }
    public ToastForm(string message, string? actionText = null, Action? action = null, int duration = 3000)
    {
        UiStyle.Apply(this);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        ControlBox = false; TopMost = true;
        ClientSize = new Size(430, actionText is null ? 96 : 122);
        var label = new Label { Text = message, AutoEllipsis = true, Location = new Point(17, 13), Size = new Size(396, 71) };
        Controls.Add(label);
        if (actionText is not null)
        {
            var link = new LinkLabel { Text = actionText, AutoSize = true, LinkColor = UiStyle.Accent, Location = new Point(17, 89) };
            link.LinkClicked += (_, _) => { Close(); action?.Invoke(); };
            Controls.Add(link);
        }
        _timer.Interval = duration;
        _timer.Tick += (_, _) => Close();
        Shown += (_, _) => _timer.Start();
        UiStyle.Position(this);
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE
        base.WndProc(ref message);
    }
    protected override void Dispose(bool disposing) { if (disposing) _timer.Dispose(); base.Dispose(disposing); }
}
