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
    private readonly ToolStripMenuItem _colorMenu;
    private readonly ToolTip _tooltip = new();
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 180 };
    private readonly Func<string, Task<SearchResults>> _load;
    private readonly List<Bookmark> _bookmarks = [];
    private readonly Dictionary<Guid, long> _displaySequence = [];
    private readonly HashSet<Guid> _collapsed = [];
    private Dictionary<Guid, BookmarkTreeRow> _rows = [];
    private readonly Dictionary<Guid, (long Revision, Bookmark? Value)> _changes = [];
    private long _changeRevision;
    private int _loadVersion;
    private bool _keepOpen;
    private Point? _dragOrigin;
    private Guid? _dragId;
    private Guid? _dropTargetId;
    private bool _dragging;
    private bool _suppressClick;
    private bool _dropHint;
    private string _normalStatus = "";
    private const string DragFormat = "WorkBookmark.BookmarkId";
    public event Action<Bookmark>? ResumeRequested;
    public event Action<Bookmark>? NoteRequested;
    public event Action<Bookmark>? DeleteRequested;
    public event Action<Bookmark>? RelinkRequested;
    public event Action<Bookmark, Guid?>? ParentChangeRequested;
    public event Action<Bookmark, BookmarkColor>? ColorChangeRequested;
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
        _items.AllowDrop = true;
        _status.SetBounds(16, 528, 520, 26);
        _status.AllowDrop = true;
        _cancel.Location = new Point(558, 528);
        Controls.AddRange([_search, _items, _status, _cancel]);
        _items.DrawItem += DrawBookmark;
        Shown += (_, _) => _items.ItemHeight = (int)Math.Round(92 * _items.DeviceDpi / 96F);
        _items.DpiChangedAfterParent += (_, _) => _items.ItemHeight = (int)Math.Round(92 * _items.DeviceDpi / 96F);
        _items.SelectedIndexChanged += (_, _) => _tooltip.SetToolTip(_items, Selected is { } selected ? $"{selected.Target.Path}\n저장: {selected.CapturedAtUtc.ToLocalTime():yyyy.MM.dd HH:mm:ss}\n다른 항목 위에 끌어 놓으면 하위 책갈피로 이동합니다." : "");
        _items.MouseDown += ItemsMouseDown;
        _items.MouseMove += ItemsMouseMove;
        _items.MouseUp += ItemsMouseUp;
        _items.DragEnter += ItemsDragOver;
        _items.DragOver += ItemsDragOver;
        _items.DragLeave += (_, _) => ClearDropTarget();
        _items.DragDrop += ItemsDragDrop;
        _status.DragEnter += RootDragOver;
        _status.DragOver += RootDragOver;
        _status.DragLeave += (_, _) => ClearDropTarget();
        _status.DragDrop += RootDragDrop;
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await ReloadAsync(); };
        _cancel.LinkClicked += (_, _) => CancelRequested?.Invoke();
        var note = new ToolStripMenuItem("메모", null, (_, _) => { if (Selected is { } value) NoteRequested?.Invoke(value); });
        var copy = new ToolStripMenuItem("전체 경로/URL 복사", null, (_, _) => { if (Selected is { } value) try { Clipboard.SetText(value.Target.Path); } catch { _status.Text = "클립보드에 복사하지 못했습니다."; } });
        var delete = new ToolStripMenuItem("목록에서 지우기", null, (_, _) => { if (Selected is { } value) DeleteRequested?.Invoke(value); });
        var relink = new ToolStripMenuItem("위치 다시 지정", null, (_, _) => { if (Selected is { } value) RelinkRequested?.Invoke(value); });
        var detach = new ToolStripMenuItem("최상위로 꺼내기", null, (_, _) => { if (Selected is { } value) RequestParentChange(value.Id, null); });
        _colorMenu = BookmarkColors.CreateMenu(() => Selected, (bookmark, color) => ColorChangeRequested?.Invoke(bookmark, color));
        _menu.Items.AddRange([note, copy, _colorMenu, detach, new ToolStripSeparator(), delete, relink]);
        _menu.Opening += (_, e) => { if (Selected is null) e.Cancel = true; BookmarkColors.UpdateMenu(_colorMenu, Selected); detach.Enabled = Selected?.ParentId is not null; relink.Visible = Selected?.LastResumeResult == ResultCode.TargetUnavailable && Selected.Target.Kind is not (TargetKind.WebPage or TargetKind.NotepadSnapshot) && !OfficeLocation.IsWebTarget(Selected.Target); copy.Text = Selected?.Target.Kind == TargetKind.NotepadSnapshot ? "보관 ID 복사" : "전체 경로/URL 복사"; _keepOpen = true; };
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
        long revision = _changeRevision;
        Guid? selected = preserveSelection ? Selected?.Id : null;
        try
        {
            // A complete snapshot keeps old parents and descendants available when
            // searching or moving branches, regardless of their capture dates.
            var result = await _load("");
            if (IsDisposed || version != _loadVersion) return;
            var merged = result.Items.Where(item => item.DeletedAtUtc is null).ToDictionary(item => item.Id);
            // A read may have started before a committed edit/delete was published.
            // Apply those newer notifications over that read instead of discarding
            // an initial load and leaving the window empty.
            foreach (var change in _changes.Where(change => change.Value.Revision > revision))
            {
                if (change.Value.Value is { DeletedAtUtc: null } current) merged[change.Key] = current;
                else merged.Remove(change.Key);
            }
            _changes.Clear();
            _bookmarks.Clear();
            _bookmarks.AddRange(merged.Values);
            _displaySequence.Clear();
            foreach (var item in _bookmarks) _displaySequence[item.Id] = item.CaptureSequence;
            _collapsed.IntersectWith(_bookmarks.Select(item => item.Id));
            ProjectItems(selected);
            if (result.HasMore) _status.Text = "일부 책갈피만 불러왔습니다. 전체 목록을 다시 불러와 주세요.";
        }
        catch { if (!IsDisposed && version == _loadVersion) _status.Text = "기록을 읽을 수 없습니다. 설정에서 데이터 폴더를 확인해 주세요."; }
    }
    // Edits replace a row in-place. Captures never reorder a currently visible list.
    public void Replace(Bookmark value)
    {
        _changes[value.Id] = (++_changeRevision, value);
        int index = _bookmarks.FindIndex(item => item.Id == value.Id);
        bool restoredParent = index < 0 && value.DeletedAtUtc is null && _bookmarks.Any(item => item.ParentId == value.Id);
        if (restoredParent)
        {
            index = _bookmarks.Count;
            _bookmarks.Add(value);
            _displaySequence[value.Id] = value.CaptureSequence;
        }
        if (index < 0) return;
        if (value.DeletedAtUtc is not null) { Remove(value.Id); return; }
        bool parentChanged = restoredParent || _bookmarks[index].ParentId != value.ParentId;
        _bookmarks[index] = value;
        if (parentChanged)
        {
            var seen = new HashSet<Guid>();
            Guid? parent = value.ParentId;
            while (parent is { } parentId && seen.Add(parentId))
            {
                _collapsed.Remove(parentId);
                parent = _bookmarks.FirstOrDefault(item => item.Id == parentId)?.ParentId;
            }
        }
        ProjectItems(Selected?.Id);
    }
    public void Remove(Guid id)
    {
        _changes[id] = (++_changeRevision, null);
        Guid? selected = Selected?.Id;
        _bookmarks.RemoveAll(item => item.Id == id);
        _displaySequence.Remove(id);
        _collapsed.Remove(id);
        ProjectItems(selected == id ? null : selected);
    }
    public void SetBusy(bool busy, string? message = null) { _cancel.Visible = busy; if (message is not null) _status.Text = message; }
    private void ProjectItems(Guid? selected)
    {
        string query = _search.Text.Trim();
        bool searching = query.Length > 0;
        var byId = _bookmarks.ToDictionary(item => item.Id);
        HashSet<Guid>? included = null;
        int matches = 0;
        if (searching)
        {
            included = [];
            foreach (var item in _bookmarks.Where(item => Matches(item, query)))
            {
                matches++;
                Bookmark? current = item;
                while (current is not null && included.Add(current.Id))
                    current = current.ParentId is { } parent && byId.TryGetValue(parent, out var ancestor) ? ancestor : null;
            }
        }
        // Keep sibling order stable while the window is open, even if a capture
        // refresh updates its timestamp. A new reload establishes the latest order.
        var projected = BookmarkTree.Flatten(_bookmarks
            .Where(item => included is null || included.Contains(item.Id))
            .Select(item => item with { CaptureSequence = _displaySequence.GetValueOrDefault(item.Id, item.CaptureSequence) }), searching ? null : _collapsed)
            .Select(row => row with { Bookmark = byId[row.Bookmark.Id] }).ToArray();
        _rows = projected.ToDictionary(row => row.Bookmark.Id);
        _items.BeginUpdate();
        try
        {
            _items.Items.Clear();
            foreach (var row in projected) _items.Items.Add(row.Bookmark);
            int index = selected.HasValue ? Array.FindIndex(projected, row => row.Bookmark.Id == selected.Value) : -1;
            _items.SelectedIndex = index >= 0 ? index : (_items.Items.Count > 0 ? 0 : -1);
        }
        finally { _items.EndUpdate(); }
        BookmarkColors.UpdateMenu(_colorMenu, Selected);
        _normalStatus = _bookmarks.Count == 0 ? "책갈피가 없습니다. 작업 창에서 저장 단축키를 눌러 주세요."
            : searching ? $"검색 결과 {matches}개 · 상위 항목 함께 표시 · Enter로 이어가기"
            : $"전체 {_bookmarks.Count}개 · 끌어서 하위로 · ▸ 접기/펼치기 · 클릭 이어가기";
        _status.Text = _normalStatus;
        _items.Invalidate();
    }
    private static bool Matches(Bookmark item, string query) =>
        item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        item.Target.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (item.Target.SheetName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
        item.Note.Contains(query, StringComparison.OrdinalIgnoreCase);
    private int Scale(int value) => (int)Math.Round(value * _items.DeviceDpi / 96F);
    private Rectangle ExpanderBounds(int index, int depth)
    {
        Rectangle row = _items.GetItemRectangle(index);
        return new Rectangle(row.Left + Scale(4 + Math.Min(depth, 14) * 20), row.Top + Scale(8), Scale(22), Scale(24));
    }
    private void ToggleBranch(Bookmark bookmark)
    {
        if (!_rows.TryGetValue(bookmark.Id, out var row) || !row.HasChildren) return;
        if (!string.IsNullOrWhiteSpace(_search.Text))
        {
            _status.Text = "검색 중에는 일치하는 항목의 상위 경로가 펼쳐집니다.";
            return;
        }
        if (!_collapsed.Add(bookmark.Id)) _collapsed.Remove(bookmark.Id);
        ProjectItems(bookmark.Id);
    }
    private void ItemsMouseDown(object? sender, MouseEventArgs e)
    {
        int index = _items.IndexFromPoint(e.Location);
        if (index >= 0) _items.SelectedIndex = index;
        if (e.Button == MouseButtons.Right) { _keepOpen = true; return; }
        if (e.Button != MouseButtons.Left) return;
        _suppressClick = false;
        _dragOrigin = null;
        _dragId = null;
        if (index < 0 || Selected is not { } item) return;
        if (_rows.TryGetValue(item.Id, out var row) && row.HasChildren && ExpanderBounds(index, row.Depth).Contains(e.Location))
        {
            _suppressClick = true;
            ToggleBranch(item);
            return;
        }
        _dragOrigin = e.Location;
        _dragId = item.Id;
    }
    private void ItemsMouseMove(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _dragOrigin is not { } origin || _dragId is not { } id || _dragging) return;
        var threshold = new Rectangle(origin.X - SystemInformation.DragSize.Width / 2, origin.Y - SystemInformation.DragSize.Height / 2,
            SystemInformation.DragSize.Width, SystemInformation.DragSize.Height);
        if (threshold.Contains(e.Location)) return;
        _suppressClick = true;
        _dragging = true;
        _keepOpen = true;
        _dropHint = true;
        _status.Text = "항목 위: 하위로 이동 · 빈 곳/이 안내줄: 최상위로 이동";
        try { _items.DoDragDrop(new DataObject(DragFormat, id.ToString()), DragDropEffects.Move); }
        finally
        {
            _dragging = false;
            _keepOpen = false;
            _dragOrigin = null;
            _dragId = null;
            ClearDropTarget();
        }
    }
    private void ItemsMouseUp(object? sender, MouseEventArgs e)
    {
        int index = _items.IndexFromPoint(e.Location);
        if (e.Button == MouseButtons.Left && !_suppressClick && !_dragging && index >= 0 &&
            _items.Items[index] is Bookmark item && _dragId == item.Id)
            ResumeRequested?.Invoke(item);
        _dragOrigin = null;
        _dragId = null;
    }
    private static Guid? DragId(DragEventArgs e) => e.Data?.GetData(DragFormat) is string text && Guid.TryParse(text, out var id) ? id : null;
    private void ItemsDragOver(object? sender, DragEventArgs e)
    {
        Point point = _items.PointToClient(new Point(e.X, e.Y));
        if (_items.Items.Count > 0)
        {
            if (point.Y < Scale(22) && _items.TopIndex > 0) _items.TopIndex--;
            else if (point.Y > _items.ClientSize.Height - Scale(22) && _items.TopIndex < _items.Items.Count - 1) _items.TopIndex++;
        }
        // Hit-test after scrolling so the outlined parent is the one a drop at
        // this exact pointer location would choose.
        int index = _items.IndexFromPoint(point);
        Guid? target = index >= 0 ? ((Bookmark)_items.Items[index]).Id : null;
        SetDropTarget(e, target);
    }
    private void RootDragOver(object? sender, DragEventArgs e) => SetDropTarget(e, null);
    private void SetDropTarget(DragEventArgs e, Guid? parentId)
    {
        _dropHint = true;
        bool allowed = DragId(e) is { } id && CanRequestParentChange(id, parentId);
        e.Effect = allowed ? DragDropEffects.Move : DragDropEffects.None;
        _dropTargetId = allowed ? parentId : null;
        _status.BackColor = allowed && parentId is null ? Color.FromArgb(220, 238, 232) : BackColor;
        _status.Text = allowed ? parentId is null ? "놓으면 최상위 책갈피로 이동합니다."
            : $"‘{_bookmarks.First(item => item.Id == parentId).DisplayName}’의 하위로 이동합니다."
            : "현재 위치이거나 자기 자신/하위 항목이라 이동할 수 없습니다.";
        _items.Invalidate();
    }
    private void ItemsDragDrop(object? sender, DragEventArgs e)
    {
        Point point = _items.PointToClient(new Point(e.X, e.Y));
        int index = _items.IndexFromPoint(point);
        Guid? parent = index >= 0 ? ((Bookmark)_items.Items[index]).Id : null;
        ClearDropTarget();
        if (DragId(e) is { } id) RequestParentChange(id, parent);
    }
    private void RootDragDrop(object? sender, DragEventArgs e)
    {
        ClearDropTarget();
        if (DragId(e) is { } id) RequestParentChange(id, null);
    }
    private bool CanRequestParentChange(Guid id, Guid? parentId) =>
        _bookmarks.FirstOrDefault(item => item.Id == id) is { } value && value.ParentId != parentId &&
        BookmarkTree.CanReparent(_bookmarks, id, parentId);
    private void RequestParentChange(Guid id, Guid? parentId)
    {
        if (!CanRequestParentChange(id, parentId)) return;
        var item = _bookmarks.First(value => value.Id == id);
        _status.Text = "구조 변경을 저장하는 중…";
        ParentChangeRequested?.Invoke(item, parentId);
    }
    private void ClearDropTarget()
    {
        _dropTargetId = null;
        _status.BackColor = BackColor;
        if (_dropHint) _status.Text = _normalStatus;
        _dropHint = false;
        _items.Invalidate();
    }
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
        if (_items.Focused && keyData is Keys.Left or Keys.Right && Selected is { } selected)
        {
            if (_rows.TryGetValue(selected.Id, out var row))
            {
                if (keyData == Keys.Left && row.HasChildren && !_collapsed.Contains(selected.Id)) ToggleBranch(selected);
                else if (keyData == Keys.Right && row.HasChildren && _collapsed.Contains(selected.Id)) ToggleBranch(selected);
                else if (keyData == Keys.Left && selected.ParentId is { } parent)
                {
                    for (int index = 0; index < _items.Items.Count; index++)
                        if (((Bookmark)_items.Items[index]).Id == parent) { _items.SelectedIndex = index; break; }
                }
                else if (keyData == Keys.Right && row.HasChildren && _items.SelectedIndex + 1 < _items.Items.Count)
                    _items.SelectedIndex++;
            }
            return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
    }
    private void DrawBookmark(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        Bookmark bookmark = (Bookmark)_items.Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        bool dropTarget = _dropTargetId == bookmark.Id;
        using var background = new SolidBrush(dropTarget ? Color.FromArgb(199, 231, 217) : selected ? Color.FromArgb(220, 238, 232) : BookmarkColors.ListPaper(bookmark.Color));
        e.Graphics.FillRectangle(background, e.Bounds);
        if (bookmark.Color != BookmarkColor.Default)
        {
            using var swatch = new SolidBrush(BookmarkColors.Get(bookmark.Color).Swatch);
            e.Graphics.FillRectangle(swatch, e.Bounds.Left + Scale(1), e.Bounds.Top + Scale(8), Scale(3), e.Bounds.Height - Scale(16));
        }
        _rows.TryGetValue(bookmark.Id, out var row);
        int depth = row?.Depth ?? 0;
        int indent = Scale(28 + Math.Min(depth, 14) * 20);
        var top = new Rectangle(e.Bounds.X + indent, e.Bounds.Y + Scale(8), e.Bounds.Width - indent - Scale(10), Scale(24));
        if (row?.HasChildren == true)
            TextRenderer.DrawText(e.Graphics, _collapsed.Contains(bookmark.Id) && string.IsNullOrWhiteSpace(_search.Text) ? "▸" : "▾",
                Font, ExpanderBounds(e.Index, depth), UiStyle.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if (depth > 0)
        {
            using var branch = new Pen(Color.FromArgb(184, 202, 194));
            int branchX = e.Bounds.Left + indent - Scale(15);
            e.Graphics.DrawLine(branch, branchX, e.Bounds.Top, branchX, e.Bounds.Top + Scale(20));
            e.Graphics.DrawLine(branch, branchX, e.Bounds.Top + Scale(20), branchX + Scale(7), e.Bounds.Top + Scale(20));
        }
        string date = RelativeTime(bookmark.CapturedAtUtc);
        string title = bookmark.DisplayName + (bookmark.Target.HadUnsavedChanges == true ? "  • 저장 당시 미저장 변경" : "");
        TextRenderer.DrawText(e.Graphics, title, Font, new Rectangle(top.X, top.Y, top.Width - Scale(106), top.Height), UiStyle.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, date, Font, new Rectangle(top.Right - Scale(102), top.Y, Scale(102), top.Height), UiStyle.Muted, TextFormatFlags.Right | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, UiStyle.Location(bookmark), Font, new Rectangle(top.X, top.Y + Scale(27), top.Width, Scale(23)), UiStyle.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, bookmark.Note, Font, new Rectangle(top.X, top.Y + Scale(52), top.Width, Scale(23)), UiStyle.Accent, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        using var line = new Pen(Color.FromArgb(226, 232, 229));
        e.Graphics.DrawLine(line, e.Bounds.Left + Scale(8), e.Bounds.Bottom - 1, e.Bounds.Right - Scale(8), e.Bounds.Bottom - 1);
        if (dropTarget || selected && bookmark.Color != BookmarkColor.Default)
        {
            using var outline = new Pen(UiStyle.Accent, Scale(dropTarget ? 2 : 1));
            e.Graphics.DrawRectangle(outline, e.Bounds.Left + 1, e.Bounds.Top + 1, e.Bounds.Width - 3, e.Bounds.Height - 3);
        }
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
