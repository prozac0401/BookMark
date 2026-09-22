using System.Drawing;
using System.Windows.Forms;
using WorkBookmark.Core;

namespace WorkBookmark.App.UI;

internal sealed class RecentForm : Form
{
    private readonly ImeTextBox _search = new() { PlaceholderText = "이름 · 경로/URL · 시트 · 메모 검색" };
    private readonly ListBox _items = new() { DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 92, IntegralHeight = false, BorderStyle = BorderStyle.None };
    private readonly Label _status = new() { ForeColor = UiStyle.Muted };
    private readonly LinkLabel _cancel = new() { Text = "요청 중단", AutoSize = true, Visible = false };
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolTip _tooltip = new();
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 180 };
    private readonly Func<string, Task<SearchResults>> _load;
    private int _loadVersion;
    private bool _keepOpen;
    public event Action<Bookmark>? ResumeRequested;
    public event Action<Bookmark>? NoteRequested;
    public event Action<Bookmark>? DeleteRequested;
    public event Action<Bookmark>? RelinkRequested;
    public event Action? CancelRequested;
    public Bookmark? Selected => _items.SelectedItem as Bookmark;
    public RecentForm(Func<string, Task<SearchResults>> load)
    {
        UiStyle.Apply(this); _load = load;
        Text = "최근 책갈피 · 업무 책갈피";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ClientSize = new Size(660, 562);
        _search.SetBounds(16, 16, 628, 28);
        _items.SetBounds(16, 58, 628, 460);
        _items.BackColor = BackColor;
        _status.SetBounds(16, 528, 520, 26);
        _cancel.Location = new Point(558, 528);
        Controls.AddRange([_search, _items, _status, _cancel]);
        _items.DrawItem += DrawBookmark;
        Shown += (_, _) => _items.ItemHeight = (int)Math.Round(92 * _items.DeviceDpi / 96F);
        _items.DpiChangedAfterParent += (_, _) => _items.ItemHeight = (int)Math.Round(92 * _items.DeviceDpi / 96F);
        _items.SelectedIndexChanged += (_, _) => _tooltip.SetToolTip(_items, Selected is { } selected ? $"{selected.Target.Path}\n저장: {selected.CapturedAtUtc.ToLocalTime():yyyy.MM.dd HH:mm:ss}" : "");
        _items.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            int index = _items.IndexFromPoint(e.Location);
            if (index >= 0) _items.SelectedIndex = index;
            _keepOpen = true;
        };
        _items.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && _items.IndexFromPoint(e.Location) >= 0 && Selected is { } value)
                ResumeRequested?.Invoke(value);
        };
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await ReloadAsync(); };
        _cancel.LinkClicked += (_, _) => CancelRequested?.Invoke();
        var note = new ToolStripMenuItem("메모", null, (_, _) => { if (Selected is { } value) NoteRequested?.Invoke(value); });
        var copy = new ToolStripMenuItem("전체 경로/URL 복사", null, (_, _) => { if (Selected is { } value) try { Clipboard.SetText(value.Target.Path); } catch { _status.Text = "클립보드에 복사하지 못했습니다."; } });
        var delete = new ToolStripMenuItem("목록에서 지우기", null, (_, _) => { if (Selected is { } value) DeleteRequested?.Invoke(value); });
        var relink = new ToolStripMenuItem("위치 다시 지정", null, (_, _) => { if (Selected is { } value) RelinkRequested?.Invoke(value); });
        _menu.Items.AddRange([note, copy, new ToolStripSeparator(), delete, relink]);
        _menu.Opening += (_, e) => { if (Selected is null) e.Cancel = true; relink.Visible = Selected?.LastResumeResult == ResultCode.TargetUnavailable && Selected.Target.Kind is not (TargetKind.WebPage or TargetKind.NotepadSnapshot) && !OfficeLocation.IsWebTarget(Selected.Target); copy.Text = Selected?.Target.Kind == TargetKind.NotepadSnapshot ? "보관 ID 복사" : "전체 경로/URL 복사"; _keepOpen = true; };
        _menu.Closed += (_, _) => _keepOpen = false;
        _items.ContextMenuStrip = _menu;
        Deactivate += (_, _) => { if (!_keepOpen && !_menu.Visible) Hide(); };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        UiStyle.Position(this);
    }
    public async Task OpenAsync()
    {
        if (!Visible)
        {
            _searchTimer.Stop();
            _search.Text = "";
            _searchTimer.Stop();
            _items.Items.Clear(); _status.Text = "기록을 불러오는 중…";
            UiStyle.Position(this); Show(); Activate(); _search.Focus();
            await ReloadAsync(false);
        }
        else { Activate(); _search.Focus(); }
    }
    public async Task ReloadAsync(bool preserveSelection = true)
    {
        int version = ++_loadVersion;
        Guid? selected = preserveSelection ? Selected?.Id : null;
        string query = _search.Text;
        try
        {
            var result = await _load(query);
            if (IsDisposed || version != _loadVersion) return;
            _items.BeginUpdate();
            _items.Items.Clear();
            foreach (var bookmark in result.Items) _items.Items.Add(bookmark);
            int index = selected.HasValue ? result.Items.ToList().FindIndex(b => b.Id == selected.Value) : -1;
            _items.SelectedIndex = index >= 0 ? index : (_items.Items.Count > 0 ? 0 : -1);
            _items.EndUpdate();
            _status.Text = result.HasMore ? (string.IsNullOrWhiteSpace(query) ? "최근 20개 · 이전 기록은 검색으로 찾을 수 있습니다." : "검색 결과 100개 · 검색어를 좁혀 주세요.") : result.Items.Count == 0 ? "책갈피가 없습니다. 작업 창에서 저장 단축키를 눌러 주세요." : query.Length == 0 ? "최근 저장 순서 · Enter/클릭 이어가기 · 우클릭 메뉴" : $"검색 결과 {result.Items.Count}개 · Enter로 이어가기";
        }
        catch { if (!IsDisposed && version == _loadVersion) _status.Text = "기록을 읽을 수 없습니다. 설정에서 데이터 폴더를 확인해 주세요."; }
    }
    // Edits replace a row in-place. Captures never reorder a currently visible list.
    public void Replace(Bookmark value)
    {
        for (int i = 0; i < _items.Items.Count; i++)
            if (((Bookmark)_items.Items[i]).Id == value.Id) { _items.Items[i] = value; break; }
    }
    public void Remove(Guid id)
    {
        for (int i = 0; i < _items.Items.Count; i++) if (((Bookmark)_items.Items[i]).Id == id)
        {
            _items.Items.RemoveAt(i);
            if (_items.Items.Count > 0) _items.SelectedIndex = Math.Min(i, _items.Items.Count - 1);
            break;
        }
    }
    public void SetBusy(bool busy, string? message = null) { _cancel.Visible = busy; if (message is not null) _status.Text = message; }
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape && !_search.IsComposing) { Hide(); return true; }
        if (keyData == Keys.Enter)
        {
            if (_search.IgnoreSubmit) return base.ProcessCmdKey(ref message, keyData);
            if (Selected is { } value) ResumeRequested?.Invoke(value);
            return true;
        }
        if (keyData is Keys.Up or Keys.Down && !_search.IsComposing)
        {
            if (_items.Items.Count > 0) _items.SelectedIndex = Math.Clamp(_items.SelectedIndex + (keyData == Keys.Down ? 1 : -1), 0, _items.Items.Count - 1);
            return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
    }
    private void DrawBookmark(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        Bookmark bookmark = (Bookmark)_items.Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? Color.FromArgb(220, 238, 232) : BackColor);
        e.Graphics.FillRectangle(background, e.Bounds);
        int Scale(int value) => (int)Math.Round(value * _items.DeviceDpi / 96F);
        var top = new Rectangle(e.Bounds.X + Scale(10), e.Bounds.Y + Scale(8), e.Bounds.Width - Scale(20), Scale(24));
        string date = RelativeTime(bookmark.CapturedAtUtc);
        string title = bookmark.DisplayName + (bookmark.Target.HadUnsavedChanges == true ? "  • 저장 당시 미저장 변경" : "");
        TextRenderer.DrawText(e.Graphics, title, Font, new Rectangle(top.X, top.Y, top.Width - Scale(106), top.Height), UiStyle.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, date, Font, new Rectangle(top.Right - Scale(102), top.Y, Scale(102), top.Height), UiStyle.Muted, TextFormatFlags.Right | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, UiStyle.Location(bookmark), Font, new Rectangle(top.X, top.Y + Scale(27), top.Width, Scale(23)), UiStyle.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, bookmark.Note, Font, new Rectangle(top.X, top.Y + Scale(52), top.Width, Scale(23)), UiStyle.Accent, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        using var line = new Pen(Color.FromArgb(226, 232, 229));
        e.Graphics.DrawLine(line, e.Bounds.Left + Scale(8), e.Bounds.Bottom - 1, e.Bounds.Right - Scale(8), e.Bounds.Bottom - 1);
    }
    private static string RelativeTime(DateTimeOffset captured)
    {
        var ago = DateTimeOffset.UtcNow - captured;
        if (ago < TimeSpan.Zero) return captured.ToLocalTime().ToString("MM.dd HH:mm");
        if (ago.TotalMinutes < 1) return "방금";
        if (ago.TotalHours < 1) return $"{(int)ago.TotalMinutes}분 전";
        if (ago.TotalDays < 1) return $"{(int)ago.TotalHours}시간 전";
        if (ago.TotalDays < 7) return $"{(int)ago.TotalDays}일 전";
        return captured.ToLocalTime().ToString("MM.dd HH:mm");
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _searchTimer.Dispose(); _menu.Dispose(); _tooltip.Dispose(); }
        base.Dispose(disposing);
    }
}
