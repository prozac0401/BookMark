using System.Drawing;
using WorkBookmark.Core;

namespace WorkBookmark.App.UI;

internal sealed class DeletedBookmarksForm : Form
{
    private readonly ListBox _items = new() { Dock = DockStyle.Fill, IntegralHeight = false, DisplayMember = nameof(Bookmark.DisplayName) };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = true, ForeColor = UiStyle.Muted };
    private readonly Button _restore = new() { Text = "선택한 책갈피 복원", AutoSize = true, Enabled = false };
    private readonly Func<Task<IReadOnlyList<Bookmark>>> _load;
    private readonly Func<Guid, Task<bool>> _restoreBookmark;
    private bool _busy;
    private int _version;

    internal DeletedBookmarksForm(Func<Task<IReadOnlyList<Bookmark>>> load, Func<Guid, Task<bool>> restore)
    {
        UiStyle.Apply(this); _load = load; _restoreBookmark = restore;
        Text = "최근 삭제 · 업무 책갈피";
        ClientSize = new Size(550, 390); MinimumSize = new Size(420, 300);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "최근 지운 책갈피를 복원할 수 있습니다. 원본 파일은 삭제되지 않습니다.", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 14) }, 0, 0);
        _items.AccessibleName = "삭제된 책갈피";
        _items.DrawMode = DrawMode.OwnerDrawFixed; _items.ItemHeight = 64;
        _items.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var item = (Bookmark)_items.Items[e.Index];
            e.DrawBackground();
            var ink = (e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText : UiStyle.Ink;
            int pad = Math.Max(6, (int)(8 * DeviceDpi / 96F));
            var title = new Rectangle(e.Bounds.X + pad, e.Bounds.Y + pad, e.Bounds.Width - 2 * pad, e.Bounds.Height / 2 - pad);
            TextRenderer.DrawText(e.Graphics, item.DisplayName, Font, title, ink, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            title.Y += e.Bounds.Height / 2 - pad; title.Height = e.Bounds.Height / 2;
            TextRenderer.DrawText(e.Graphics, UiStyle.Location(item), Font, title, ink, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        };
        _items.SelectedIndexChanged += (_, _) => _restore.Enabled = !_busy && _items.SelectedItem is Bookmark;
        _restore.Click += async (_, _) => await RestoreSelectedAsync();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 12, 0, 0) };
        var close = new Button { Text = "닫기", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(close); buttons.Controls.Add(_restore);
        _status.Margin = new Padding(0, 10, 0, 0);
        layout.Controls.Add(_items, 0, 1); layout.Controls.Add(_status, 0, 2); layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout); AcceptButton = _restore; CancelButton = close;
        Shown += async (_, _) => { _items.ItemHeight = (int)Math.Round(64 * DeviceDpi / 96F); await ReloadAsync(); };
        DpiChanged += (_, _) => _items.ItemHeight = (int)Math.Round(64 * DeviceDpi / 96F);
    }

    internal async Task ReloadAsync()
    {
        int version = ++_version;
        try
        {
            var items = await _load();
            if (IsDisposed || version != _version) return;
            _items.Items.Clear(); foreach (var item in items) _items.Items.Add(item);
            if (_items.Items.Count > 0) _items.SelectedIndex = 0;
            _status.Text = items.Count == 0 ? "최근 지운 책갈피가 없습니다." : $"최근 삭제 {items.Count}개 · 최대 100개 표시";
        }
        catch { if (!IsDisposed) _status.Text = "삭제한 책갈피를 읽지 못했습니다. 창을 다시 열어 주세요."; }
    }

    private async Task RestoreSelectedAsync()
    {
        if (_busy || _items.SelectedItem is not Bookmark item) return;
        _busy = true; _restore.Enabled = false;
        try
        {
            bool restored = await _restoreBookmark(item.Id);
            if (IsDisposed) return;
            if (restored) await ReloadAsync(); else _status.Text = "복원하지 못했습니다. 잠시 후 다시 시도해 주세요.";
        }
        finally { _busy = false; if (!IsDisposed) _restore.Enabled = _items.SelectedItem is Bookmark; }
    }
}
