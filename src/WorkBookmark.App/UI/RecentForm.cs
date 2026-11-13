using System.Drawing;
using System.Windows.Forms;
using WorkBookmark.Core;

namespace WorkBookmark.App.UI;

internal sealed class RecentForm : Form
{
    private readonly ImeTextBox _search = new() { PlaceholderText = "이름 · 경로/URL · 시트 · 메모 검색" };
    private readonly Button _newFolder = new() { Text = "새 폴더", AccessibleName = "최상위 책갈피 폴더 만들기" };
    private readonly SelectionListBox _items = new() { DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 92, IntegralHeight = false, BorderStyle = BorderStyle.None };
    private readonly Label _status = new() { ForeColor = UiStyle.Muted };
    private readonly LinkLabel _cancel = new() { Text = "요청 중단", AutoSize = true, Visible = false };
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _colorMenu;
    private readonly ToolStripMenuItem _folderToggleMenu;
    private readonly ToolStripMenuItem _createFolderMenu;
    private readonly ToolStripMenuItem _renameFolderMenu;
    private readonly ToolTip _tooltip = new();
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 180 };
    private readonly System.Windows.Forms.Timer _dragScrollTimer = new() { Interval = 300 };
    private readonly Func<string, Task<SearchResults>> _load;
    private readonly List<Bookmark> _bookmarks = [];
    private readonly Dictionary<Guid, long> _displaySequence = [];
    private readonly HashSet<Guid> _collapsed = [];
    private readonly HashSet<Guid> _selectedIds = [];
    private Dictionary<Guid, BookmarkTreeRow> _rows = [];
    private readonly Dictionary<Guid, (long Revision, Bookmark? Value)> _changes = [];
    private long _changeRevision;
    private int _loadVersion;
    private bool _isDisposing;
    private bool _keepOpen;
    private bool _applicationDeactivated;
    private bool _deactivationHideQueued;
    private Point? _dragOrigin;
    private Guid? _dragId;
    private Guid[] _dropSourceIds = [];
    private bool _collapseSelectionOnRelease;
    private bool _selectionUpdating;
    private Guid? _dropTargetId;
    private BookmarkDropRequest? _dropRequest;
    private bool _dropInsertion, _dropAfter;
    private bool _dragging;
    private bool _suppressClick;
    private bool _dropHint;
    private Point _dragScrollPoint;
    private Guid? _dragScrollId;
    private int _dragScrollDirection;
    private bool _projectionPending;
    private Guid? _projectionSelection;
    private string _normalStatus = "";
    private const string DragFormat = "WorkBookmark.BookmarkId";
    private const string MultiDragFormat = "WorkBookmark.BookmarkIds";
    public event Action<Bookmark>? ResumeRequested;
    public event Action<Bookmark>? NoteRequested;
    public event Action<Bookmark>? DeleteRequested;
    public event Action<Bookmark>? RelinkRequested;
    public event Action<Bookmark, Guid?>? ParentChangeRequested;
    public event Action<BookmarkDropRequest>? DropRequested;
    public event Action? UndoRequested;
    public event Action<Bookmark, BookmarkColor>? ColorChangeRequested;
    public event Action<Bookmark>? FolderToggleRequested;
    public event Action<Guid?>? CreateFolderRequested;
    public event Action<Bookmark>? RenameFolderRequested;
    public event Action<Bookmark, BookmarkSortMode>? SortChildrenRequested;
    public event Action<Bookmark>? ShowStickerRequested;
    public event Action? CancelRequested;
    public Bookmark? Selected => _items.SelectedItem as Bookmark;
    internal IReadOnlyCollection<Guid> SelectedIds => _selectedIds;
    internal Bookmark? FindBookmark(Guid id) => _bookmarks.FirstOrDefault(item => item.Id == id);
    public RecentForm(Func<string, Task<SearchResults>> load)
    {
        UiStyle.Apply(this); _load = load;
        Text = "최근 책갈피 · 업무 책갈피";
        TopMost = true;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ClientSize = new Size(660, 562);
        _search.SetBounds(16, 16, 520, 28);
        _newFolder.SetBounds(548, 15, 96, 30);
        _items.SetBounds(16, 58, 628, 460);
        _items.BackColor = BackColor;
        _items.AllowDrop = true;
        _status.SetBounds(16, 528, 520, 26);
        _status.AllowDrop = true;
        _cancel.Location = new Point(558, 528);
        Controls.AddRange([_search, _newFolder, _items, _status, _cancel]);
        _items.DrawItem += DrawBookmark;
        Shown += (_, _) => _items.ItemHeight = (int)Math.Round(92 * _items.DeviceDpi / 96F);
        _items.DpiChangedAfterParent += (_, _) => _items.ItemHeight = (int)Math.Round(92 * _items.DeviceDpi / 96F);
        _items.SelectedIndexChanged += (_, _) =>
        {
            if (_isDisposing) return;
            // Native mouse selection only tracks the focused row. Keyboard and
            // accessibility selection still replace the selected set normally.
            if (!_selectionUpdating && !_dragging && !_items.PointerDispatch && Control.MouseButtons == MouseButtons.None)
            {
                _selectedIds.Clear();
                if (Selected is { } focused) _selectedIds.Add(focused.Id);
                UpdateSelectionStatus();
                _items.Invalidate();
            }
            _tooltip.SetToolTip(_items, Selected is { } selected ? selected.IsFolder
                ? $"{selected.DisplayName}\n책갈피 폴더 · 클릭하여 열기/닫기\n항목을 끌어 놓으면 이 폴더에 넣습니다."
                : $"{selected.Target.Path}\n저장: {selected.CapturedAtUtc.ToLocalTime():yyyy.MM.dd HH:mm:ss}\n행 사이: 순서 변경 · 폴더 가운데: 넣기 · 일반 항목 가운데: 묶기" : "");
        };
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
        _dragScrollTimer.Tick += (_, _) => ScrollDragItems();
        VisibleChanged += (_, _) => { if (!_isDisposing && !Visible) ClearDropTarget(); };
        _search.TextChanged += (_, _) => { if (_isDisposing) return; _searchTimer.Stop(); _searchTimer.Start(); };
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await ReloadAsync(); };
        _cancel.LinkClicked += (_, _) => CancelRequested?.Invoke();
        _newFolder.Click += (_, _) => CreateFolderRequested?.Invoke(null);
        var note = new ToolStripMenuItem("메모", null, (_, _) => { if (Selected is { } value) NoteRequested?.Invoke(value); });
        var copy = new ToolStripMenuItem("전체 경로/URL 복사", null, (_, _) => { if (Selected is { IsFolder: false } value) try { Clipboard.SetText(value.Target.Path); } catch { _status.Text = "클립보드에 복사하지 못했습니다."; } });
        var delete = new ToolStripMenuItem("목록에서 지우기", null, (_, _) => { if (Selected is { } value) DeleteRequested?.Invoke(value); });
        var relink = new ToolStripMenuItem("위치 다시 지정", null, (_, _) => { if (Selected is { IsFolder: false } value) RelinkRequested?.Invoke(value); });
        var detach = new ToolStripMenuItem("최상위로 꺼내기", null, (_, _) => { if (Selected is { } value) RequestParentChange(value.Id, null); });
        _colorMenu = BookmarkColors.CreateMenu(() => Selected, (bookmark, color) => ColorChangeRequested?.Invoke(bookmark, color));
        _folderToggleMenu = new ToolStripMenuItem("폴더 열기", null, (_, _) => { if (Selected is { IsFolder: true } value) ToggleBranch(value); });
        _createFolderMenu = new ToolStripMenuItem("새 폴더 만들기", null, (_, _) => CreateFolderRequested?.Invoke(Selected is { IsFolder: true } folder ? folder.Id : null));
        _renameFolderMenu = new ToolStripMenuItem("폴더 이름 바꾸기", null, (_, _) => { if (Selected is { IsFolder: true } value) RenameFolderRequested?.Invoke(value); });
        var undo = new ToolStripMenuItem("마지막 변경 되돌리기", null, (_, _) => UndoRequested?.Invoke()) { ShortcutKeyDisplayString = "Ctrl+Z" };
        var showSticker = new ToolStripMenuItem("스티커에서 보기", null, (_, _) => { if (Selected is { } value) ShowStickerRequested?.Invoke(value); });
        var sort = new ToolStripMenuItem("하위 항목 정렬");
        sort.DropDownItems.Add("원본 이름순 정렬", null, (_, _) => { if (Selected is { IsFolder: true } value) SortChildrenRequested?.Invoke(value, BookmarkSortMode.Name); });
        sort.DropDownItems.Add("최근 저장순", null, (_, _) => { if (Selected is { IsFolder: true } value) SortChildrenRequested?.Invoke(value, BookmarkSortMode.Recent); });
        _menu.Items.AddRange([_folderToggleMenu, _createFolderMenu, _renameFolderMenu, sort, showSticker, note, copy, _colorMenu, detach, new ToolStripSeparator(), delete, relink, undo]);
        _menu.Opening += (_, _) =>
        {
            bool folder = Selected?.IsFolder == true;
            sort.Visible = folder;
            showSticker.Visible = Selected is not null;
            _folderToggleMenu.Visible = _renameFolderMenu.Visible = folder;
            _folderToggleMenu.Text = Selected?.FolderExpanded == true ? "폴더 닫기" : "폴더 열기";
            _createFolderMenu.Text = folder ? "하위 폴더 만들기" : "새 폴더 만들기";
            note.Visible = Selected is not null && !folder;
            copy.Visible = Selected is not null && !folder;
            delete.Visible = detach.Visible = _colorMenu.Visible = Selected is not null;
            BookmarkColors.UpdateMenu(_colorMenu, Selected);
            detach.Enabled = Selected?.ParentId is not null;
            relink.Visible = Selected is { IsFolder: false, LastResumeResult: ResultCode.TargetUnavailable } value &&
                value.Target.Kind is not (TargetKind.WebPage or TargetKind.NotepadSnapshot) && !OfficeLocation.IsWebTarget(value.Target);
            copy.Text = Selected?.Target.Kind == TargetKind.NotepadSnapshot ? "보관 ID 복사" : "전체 경로/URL 복사";
            _keepOpen = true;
        };
        _menu.Closed += (_, _) => { _keepOpen = false; QueueApplicationDeactivateHide(); };
        _items.ContextMenuStrip = _menu;
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        UiStyle.Position(this);
    }
    protected override void WndProc(ref Message message)
    {
        // WM_ACTIVATEAPP distinguishes leaving the application from switching
        // between its list, stickers and dialogs. Form.Deactivate cannot do that.
        // https://learn.microsoft.com/windows/win32/winmsg/wm-activateapp
        bool applicationActivation = message.Msg == 0x001C;
        if (applicationActivation) _applicationDeactivated = message.WParam == nint.Zero;
        base.WndProc(ref message);
        if (applicationActivation) QueueApplicationDeactivateHide();
    }
    private void QueueApplicationDeactivateHide()
    {
        if (_isDisposing || !_applicationDeactivated || _keepOpen || _menu.Visible || _dragging || _deactivationHideQueued ||
            !Visible || !IsHandleCreated || IsDisposed || Disposing) return;
        _deactivationHideQueued = true;
        // Finish native activation dispatch before hiding a window. Recheck the
        // current app state so a quick return cannot apply a stale dismissal.
        BeginInvoke((Action)(() =>
        {
            _deactivationHideQueued = false;
            if (!_isDisposing && _applicationDeactivated && !_keepOpen && !_menu.Visible && !_dragging && Visible && !IsDisposed && !Disposing)
                Hide();
        }));
    }
    public async Task OpenAsync()
    {
        if (_isDisposing || IsDisposed) return;
        if (!Visible)
        {
            _searchTimer.Stop();
            _search.Text = "";
            _searchTimer.Stop();
            _items.Items.Clear();
            _selectedIds.Clear();
            if (_isDisposing || IsDisposed) return;
            _status.Text = "기록을 불러오는 중…";
            UiStyle.Position(this); Show(); Activate(); _search.Focus();
            await ReloadAsync(false);
        }
        else { Activate(); _search.Focus(); }
    }
    public async Task ReloadAsync(bool preserveSelection = true)
    {
        if (_isDisposing || IsDisposed) return;
        int version = ++_loadVersion;
        long revision = _changeRevision;
        Guid? selected = preserveSelection ? Selected?.Id : null;
        if (!preserveSelection) _selectedIds.Clear();
        try
        {
            // A complete snapshot keeps old parents and descendants available when
            // searching or moving branches, regardless of their capture dates.
            var result = await _load("");
            if (_isDisposing || IsDisposed || version != _loadVersion) return;
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
        catch { if (!_isDisposing && !IsDisposed && version == _loadVersion) _status.Text = "기록을 읽을 수 없습니다. 설정에서 데이터 폴더를 확인해 주세요."; }
    }
    // Edits replace a row in-place. Captures never reorder a currently visible list.
    public void Replace(Bookmark value)
    {
        if (_isDisposing || IsDisposed) return;
        _changes[value.Id] = (++_changeRevision, value);
        int index = _bookmarks.FindIndex(item => item.Id == value.Id);
        bool restoredParent = index < 0 && value.DeletedAtUtc is null && (value.IsFolder || _bookmarks.Any(item => item.ParentId == value.Id));
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
                var ancestor = _bookmarks.FirstOrDefault(item => item.Id == parentId);
                if (ancestor?.IsFolder != true) _collapsed.Remove(parentId);
                parent = ancestor?.ParentId;
            }
        }
        ProjectItems(Selected?.Id);
    }
    public void ApplyFolderChanges(IReadOnlyList<Bookmark> values)
    {
        if (_isDisposing || IsDisposed) return;
        Guid? selected = Selected?.Id;
        Guid? createdFolder = null;
        foreach (var value in values)
        {
            _changes[value.Id] = (++_changeRevision, value);
            int index = _bookmarks.FindIndex(item => item.Id == value.Id);
            if (value.DeletedAtUtc is not null)
            {
                if (index >= 0) _bookmarks.RemoveAt(index);
                _displaySequence.Remove(value.Id);
                _collapsed.Remove(value.Id);
            }
            else if (index >= 0) _bookmarks[index] = value;
            else
            {
                _bookmarks.Add(value);
                _displaySequence[value.Id] = value.CaptureSequence;
                if (value.IsFolder) createdFolder ??= value.Id;
            }
        }
        if (createdFolder is Guid created)
        {
            _selectedIds.Clear();
            _selectedIds.Add(created);
        }
        ProjectItems(createdFolder ?? selected);
    }
    public void Remove(Guid id)
    {
        if (_isDisposing || IsDisposed) return;
        _changes[id] = (++_changeRevision, null);
        Guid? selected = Selected?.Id;
        _bookmarks.RemoveAll(item => item.Id == id);
        _displaySequence.Remove(id);
        _collapsed.Remove(id);
        ProjectItems(selected == id ? null : selected);
    }
    public void SetBusy(bool busy, string? message = null)
    {
        if (_isDisposing || IsDisposed) return;
        _cancel.Visible = busy;
        if (message is not null) _status.Text = message;
    }
    private void ProjectItems(Guid? selected)
    {
        if (_isDisposing || IsDisposed) return;
        // Committed data still updates immediately for drop validation. Keep the
        // displayed rows fixed until the pointer is released so an in-flight
        // search or edit cannot move a different target under the drag.
        if (_dragging)
        {
            _projectionPending = true;
            _projectionSelection = selected;
            return;
        }
        string query = _search.Text.Trim();
        bool searching = query.Length > 0;
        // Virtual folder visibility is shared, committed data. Local expansion
        // remains only for legacy ordinary parents while old snapshots refresh.
        foreach (var folder in _bookmarks.Where(item => item.IsFolder))
        {
            if (folder.FolderExpanded) _collapsed.Remove(folder.Id);
            else _collapsed.Add(folder.Id);
        }
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
        _selectedIds.IntersectWith(_rows.Keys);
        _items.BeginUpdate();
        _selectionUpdating = true;
        try
        {
            _items.Items.Clear();
            if (_isDisposing || IsDisposed) return;
            foreach (var row in projected)
            {
                if (_isDisposing || IsDisposed) return;
                _items.Items.Add(row.Bookmark);
            }
            if (_isDisposing || IsDisposed) return;
            int index = selected.HasValue ? Array.FindIndex(projected, row => row.Bookmark.Id == selected.Value) : -1;
            if (index < 0 && _selectedIds.Count > 0)
                index = Array.FindIndex(projected, row => _selectedIds.Contains(row.Bookmark.Id));
            var visited = new HashSet<Guid>();
            while (index < 0 && selected is { } hidden && visited.Add(hidden) && byId.TryGetValue(hidden, out var current) && current.ParentId is { } parent)
            {
                selected = parent;
                index = Array.FindIndex(projected, row => row.Bookmark.Id == parent);
            }
            _items.SelectedIndex = index >= 0 ? index : (_items.Items.Count > 0 ? 0 : -1);
            if (_selectedIds.Count == 0 && Selected is { } focused) _selectedIds.Add(focused.Id);
        }
        finally { _selectionUpdating = false; if (!_items.IsDisposed) _items.EndUpdate(); }
        if (_isDisposing || IsDisposed) return;
        BookmarkColors.UpdateMenu(_colorMenu, Selected);
        _normalStatus = _bookmarks.Count == 0 ? "새 폴더를 만들거나 작업 창에서 책갈피를 저장해 주세요."
            : searching ? $"검색 결과 {matches}개 · 상위 폴더 함께 표시"
            : $"전체 {_bookmarks.Count}개 · Ctrl+클릭: 여러 개 선택 · 드래그: 이동";
        UpdateSelectionStatus();
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
        if (!_rows.TryGetValue(bookmark.Id, out var row) || !bookmark.IsFolder && !row.HasChildren) return;
        if (!string.IsNullOrWhiteSpace(_search.Text))
        {
            _status.Text = "검색 중에는 일치하는 항목의 상위 경로가 펼쳐집니다.";
            return;
        }
        if (bookmark.IsFolder)
        {
            _status.Text = bookmark.FolderExpanded ? "폴더를 닫는 중…" : "폴더를 여는 중…";
            FolderToggleRequested?.Invoke(bookmark);
            return;
        }
        if (!_collapsed.Add(bookmark.Id)) _collapsed.Remove(bookmark.Id);
        ProjectItems(bookmark.Id);
    }
    private void ActivateBookmark(Bookmark bookmark)
    {
        if (bookmark.IsFolder) ToggleBranch(bookmark);
        else ResumeRequested?.Invoke(bookmark);
    }
    private void ItemsMouseDown(object? sender, MouseEventArgs e) => HandleItemsMouseDown(e, ModifierKeys);
    internal void HandleItemsMouseDown(MouseEventArgs e, Keys modifiers)
    {
        int index = _items.IndexFromPoint(e.Location);
        if (e.Button == MouseButtons.Right)
        {
            SelectOnly(index);
            _keepOpen = true;
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        _suppressClick = false;
        _collapseSelectionOnRelease = false;
        _dragOrigin = null;
        _dragId = null;
        if (index < 0) { SelectOnly(-1); return; }
        var item = (Bookmark)_items.Items[index];
        FocusIndex(index);
        bool toggle = (modifiers & Keys.Control) != 0;
        if (toggle)
        {
            _suppressClick = true;
            if (!_selectedIds.Add(item.Id))
            {
                _selectedIds.Remove(item.Id);
                FocusIndex(Enumerable.Range(0, _items.Items.Count)
                    .FirstOrDefault(candidate => _selectedIds.Contains(((Bookmark)_items.Items[candidate]).Id), -1));
                UpdateSelectionStatus();
                _items.Invalidate();
                return;
            }
        }
        else if (!_selectedIds.Contains(item.Id)) SelectOnly(index);
        else _collapseSelectionOnRelease = _selectedIds.Count > 1;
        UpdateSelectionStatus();
        _items.Invalidate();
        // Ctrl selects folders too; it must never expand them or launch a target.
        if (!toggle && _rows.TryGetValue(item.Id, out var row) && (item.IsFolder || row.HasChildren) && ExpanderBounds(index, row.Depth).Contains(e.Location))
        {
            SelectOnly(index);
            _suppressClick = true;
            ToggleBranch(item);
            return;
        }
        _dragOrigin = e.Location;
        _dragId = item.Id;
    }
    private void SelectOnly(int index)
    {
        _selectedIds.Clear();
        FocusIndex(index);
        if (index >= 0) _selectedIds.Add(((Bookmark)_items.Items[index]).Id);
        UpdateSelectionStatus();
        _items.Invalidate();
    }
    private void FocusIndex(int index)
    {
        bool changing = _selectionUpdating;
        _selectionUpdating = true;
        try { _items.SelectedIndex = index; }
        finally { _selectionUpdating = changing; }
    }
    private void UpdateSelectionStatus()
    {
        if (!_dropHint) _status.Text = _selectedIds.Count > 1
            ? $"{_selectedIds.Count}개 선택 · 선택한 항목을 드래그하여 함께 이동 · Ctrl+클릭: 선택 해제"
            : _normalStatus;
    }
    internal DataObject CreateDragData(Guid id)
    {
        Guid[] ids = _selectedIds.Contains(id) ? _items.Items.Cast<Bookmark>().Where(item => _selectedIds.Contains(item.Id)).Select(item => item.Id).ToArray() : [id];
        var data = new DataObject(DragFormat, id.ToString());
        data.SetData(MultiDragFormat, string.Join("\n", ids));
        return data;
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
        _status.Text = "폴더 위: 넣기 · 일반 항목 위: 새 폴더 · 빈 곳: 꺼내기";
        try { _items.DoDragDrop(CreateDragData(id), DragDropEffects.Move); }
        finally { FinishDrag(); }
    }
    private void FinishDrag()
    {
        _dragging = false;
        _keepOpen = false;
        _dragOrigin = null;
        _dragId = null;
        _dropSourceIds = [];
        _collapseSelectionOnRelease = false;
        ClearDropTarget();
        if (_projectionPending)
        {
            _projectionPending = false;
            ProjectItems(_projectionSelection);
            _projectionSelection = null;
        }
        QueueApplicationDeactivateHide();
    }
    private void ItemsMouseUp(object? sender, MouseEventArgs e)
    {
        int index = _items.IndexFromPoint(e.Location);
        if (e.Button == MouseButtons.Left && !_suppressClick && !_dragging && index >= 0 &&
            _items.Items[index] is Bookmark item && _dragId == item.Id)
        {
            if (_collapseSelectionOnRelease) SelectOnly(index);
            else ActivateBookmark(item);
        }
        _collapseSelectionOnRelease = false;
        _dragOrigin = null;
        _dragId = null;
    }
    private static Guid? DragId(DragEventArgs e) => e.Data?.GetData(DragFormat) is string text && Guid.TryParse(text, out var id) ? id : null;
    private Guid? ReadDragSources(DragEventArgs e)
    {
        Guid? id = DragId(e);
        _dropSourceIds = id is Guid single ? [single] : [];
        if (id is null || e.Data?.GetData(MultiDragFormat) is not string text) return id;
        var ids = new List<Guid>();
        foreach (string value in text.Split('\n'))
        {
            if (!Guid.TryParse(value, out Guid parsed) || parsed == Guid.Empty) { _dropSourceIds = []; return null; }
            ids.Add(parsed);
        }
        if (!ids.Contains(id.Value)) { _dropSourceIds = []; return null; }
        _dropSourceIds = ids.Distinct().ToArray();
        return id;
    }
    private IReadOnlyList<Guid> DropSources(Guid id) => _dropSourceIds.Contains(id) ? _dropSourceIds : [id];
    private IReadOnlyList<Guid> DropRoots(Guid id)
    {
        var sources = DropSources(id);
        return sources.All(source => FindBookmark(source) is { DeletedAtUtc: null })
            ? BookmarkTree.SelectionRoots(_bookmarks, sources) : [];
    }
    private BookmarkDropRequest IncludeSelection(BookmarkDropRequest request) => request with
    {
        BookmarkIds = DropSources(request.BookmarkId).Count > 1 ? DropSources(request.BookmarkId).ToArray() : null
    };
    private void ItemsDragOver(object? sender, DragEventArgs e)
    {
        Point point = _items.PointToClient(new Point(e.X, e.Y));
        Guid? id = ReadDragSources(e);
        UpdateDragScroll(id, point);
        e.Effect = UpdateDropAt(id, point) ? DragDropEffects.Move : DragDropEffects.None;
    }
    private void UpdateDragScroll(Guid? id, Point point)
    {
        int direction = id is not null && _bookmarks.Any(item => item.Id == id) && _items.ClientRectangle.Contains(point)
            ? point.Y < Scale(22) ? -1 : point.Y > _items.ClientSize.Height - Scale(22) ? 1 : 0
            : 0;
        if (direction != _dragScrollDirection)
        {
            StopDragScroll();
            _dragScrollDirection = direction;
            if (direction != 0)
            {
                // Give edge rows time to receive a drop before scrolling. Pointer
                // event frequency must not control the speed of the whole list.
                _dragScrollTimer.Interval = 300;
                _dragScrollTimer.Start();
            }
        }
        _dragScrollId = id;
        _dragScrollPoint = point;
    }
    private void ScrollDragItems()
    {
        if (_isDisposing || _dragScrollDirection == 0 || _dragScrollId is not { } id || !Visible || _items.Items.Count == 0) return;
        int before = _items.TopIndex;
        int next = Math.Clamp(before + _dragScrollDirection, 0, _items.Items.Count - 1);
        if (next == before) return;
        _items.TopIndex = next;
        _dragScrollTimer.Interval = 120;
        // Re-hit-test after each timed scroll, including when the pointer is still.
        UpdateDropAt(id, _dragScrollPoint);
    }
    private void StopDragScroll()
    {
        _dragScrollTimer.Stop();
        _dragScrollDirection = 0;
        _dragScrollId = null;
    }
    private void RootDragOver(object? sender, DragEventArgs e)
    {
        StopDragScroll();
        SetDropTarget(e, null);
    }
    private void SetDropTarget(DragEventArgs e, Guid? parentId)
        => e.Effect = UpdateDropTarget(ReadDragSources(e), parentId) ? DragDropEffects.Move : DragDropEffects.None;
    private bool UpdateDropAt(Guid? sourceId, Point point)
    {
        int index = _items.IndexFromPoint(point);
        if (index < 0) return UpdateDropTarget(sourceId, null);
        var target = (Bookmark)_items.Items[index];
        Rectangle bounds = _items.GetItemRectangle(index);
        int edge = Math.Min(Scale(18), bounds.Height / 4);
        bool before = point.Y < bounds.Top + edge;
        bool after = point.Y >= bounds.Bottom - edge;
        if (!before && !after) return UpdateDropTarget(sourceId, target.Id);
        BookmarkDropRequest? request = null;
        var roots = sourceId is Guid source ? DropRoots(source) : [];
        if (sourceId is Guid id && roots.Count > 0 && roots.All(root => BookmarkTree.CanDropOnto(_bookmarks, root, target.Id) &&
            BookmarkTree.CanReparent(_bookmarks, root, target.ParentId)))
        {
            Guid? beforeId = target.Id;
            if (after)
            {
                var siblings = BookmarkTree.Flatten(_bookmarks).Select(row => row.Bookmark)
                    .Where(item => item.ParentId == target.ParentId && !roots.Contains(item.Id)).ToArray();
                int targetIndex = Array.FindIndex(siblings, item => item.Id == target.Id);
                beforeId = targetIndex + 1 < siblings.Length ? siblings[targetIndex + 1].Id : null;
            }
            request = IncludeSelection(new(id, BookmarkDropKind.InsertBefore, target.ParentId, beforeId));
        }
        return SetDropPreview(request, target.Id, insertion: true, after);
    }
    private bool UpdateDropTarget(Guid? sourceId, Guid? parentId)
    {
        bool allowed = sourceId is { } id && CanRequestParentChange(id, parentId);
        Bookmark? target = parentId is Guid targetId ? FindBookmark(targetId) : null;
        BookmarkDropRequest? request = allowed ? IncludeSelection(new(sourceId!.Value,
            parentId is null ? BookmarkDropKind.Detach : target?.IsFolder == true ? BookmarkDropKind.MoveInto : BookmarkDropKind.Group,
            parentId)) : null;
        return SetDropPreview(request, parentId, insertion: false, after: false);
    }
    private bool SetDropPreview(BookmarkDropRequest? request, Guid? targetId, bool insertion, bool after)
    {
        _dropHint = true;
        bool allowed = request is not null;
        Guid? previous = _dropTargetId;
        bool previousInsertion = _dropInsertion, previousAfter = _dropAfter;
        _dropTargetId = allowed ? targetId : null;
        _dropRequest = request;
        _dropInsertion = insertion && allowed;
        _dropAfter = after && allowed;
        _status.BackColor = allowed && targetId is null ? Color.FromArgb(220, 238, 232) : BackColor;
        Bookmark? destination = targetId is { } parent ? FindBookmark(parent) : null;
        _status.Text = allowed ? insertion ? $"‘{destination?.DisplayName}’ {(after ? "뒤" : "앞")}에 놓습니다."
            : targetId is null ? "놓으면 최상위 책갈피로 이동합니다."
            : destination?.IsFolder == true ? $"‘{destination.DisplayName}’ 폴더에 넣습니다."
            : $"‘{destination?.DisplayName}’ 항목과 새 폴더로 묶습니다."
            : "현재 위치이거나 자기 자신/하위 항목이라 이동할 수 없습니다.";
        if (allowed && request!.SourceIds.Count > 1) _status.Text = $"{request.SourceIds.Count}개 함께 이동 · " + _status.Text;
        if (previous != _dropTargetId || previousInsertion != _dropInsertion || previousAfter != _dropAfter)
        {
            InvalidateDropRow(previous);
            if (previous != _dropTargetId) InvalidateDropRow(_dropTargetId);
        }
        return allowed;
    }
    private void InvalidateDropRow(Guid? id)
    {
        if (id is null) return;
        for (int index = 0; index < _items.Items.Count; index++)
        {
            if (((Bookmark)_items.Items[index]).Id != id) continue;
            Rectangle bounds = Rectangle.Intersect(_items.ClientRectangle, _items.GetItemRectangle(index));
            if (bounds.Width > 0 && bounds.Height > 0) _items.Invalidate(bounds);
            return;
        }
    }
    private void ItemsDragDrop(object? sender, DragEventArgs e)
    {
        Point point = _items.PointToClient(new Point(e.X, e.Y));
        UpdateDropAt(ReadDragSources(e), point);
        var request = _dropRequest;
        ClearDropTarget();
        if (request is not null) RequestDrop(request);
    }
    private void RootDragDrop(object? sender, DragEventArgs e)
    {
        Guid? id = ReadDragSources(e);
        UpdateDropTarget(id, null);
        var request = _dropRequest;
        ClearDropTarget();
        if (request is not null) RequestDrop(request);
    }
    private bool CanRequestParentChange(Guid id, Guid? parentId)
    {
        var roots = DropRoots(id);
        return roots.Count > 0 && (parentId is { } target
            ? roots.All(root => BookmarkTree.CanDropOnto(_bookmarks, root, target))
            : roots.Any(root => FindBookmark(root)?.ParentId is not null) && roots.All(root => BookmarkTree.CanReparent(_bookmarks, root, null)));
    }
    private void RequestParentChange(Guid id, Guid? parentId)
    {
        _dropSourceIds = [id];
        if (!CanRequestParentChange(id, parentId)) return;
        var item = _bookmarks.First(value => value.Id == id);
        var target = parentId is Guid targetId ? FindBookmark(targetId) : null;
        RequestDrop(new(item.Id, parentId is null ? BookmarkDropKind.Detach
            : target?.IsFolder == true ? BookmarkDropKind.MoveInto : BookmarkDropKind.Group, parentId));
    }
    private void RequestDrop(BookmarkDropRequest request)
    {
        _status.Text = "구조 변경을 저장하는 중…";
        if (DropRequested is not null) DropRequested.Invoke(request);
        else if (request.SourceIds.Count == 1 && request.Kind != BookmarkDropKind.InsertBefore && FindBookmark(request.BookmarkId) is { } item)
            ParentChangeRequested?.Invoke(item, request.TargetId);
    }
    private void ClearDropTarget()
    {
        StopDragScroll();
        Guid? previous = _dropTargetId;
        _dropTargetId = null;
        _dropRequest = null;
        _dropSourceIds = [];
        _dropInsertion = _dropAfter = false;
        _status.BackColor = BackColor;
        bool restoreStatus = _dropHint;
        _dropHint = false;
        if (restoreStatus) UpdateSelectionStatus();
        InvalidateDropRow(previous);
    }
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Z) && !_search.ContainsFocus) { UndoRequested?.Invoke(); return true; }
        if (keyData == Keys.Escape && !_search.IsComposing) { Hide(); return true; }
        if (_newFolder.Focused && keyData is Keys.Enter or Keys.Space)
        {
            _newFolder.PerformClick();
            return true;
        }
        if (keyData == Keys.Enter)
        {
            if (_search.IgnoreSubmit) return base.ProcessCmdKey(ref message, keyData);
            if (Selected is { } value) ActivateBookmark(value);
            return true;
        }
        if (keyData is Keys.Up or Keys.Down && !_search.IsComposing)
        {
            if (_items.Items.Count > 0) SelectOnly(Math.Clamp(_items.SelectedIndex + (keyData == Keys.Down ? 1 : -1), 0, _items.Items.Count - 1));
            return true;
        }
        if (_items.Focused && keyData is Keys.Left or Keys.Right && Selected is { } selected)
        {
            if (_rows.TryGetValue(selected.Id, out var row))
            {
                if (keyData == Keys.Left && (selected.IsFolder || row.HasChildren) && !_collapsed.Contains(selected.Id)) ToggleBranch(selected);
                else if (keyData == Keys.Right && (selected.IsFolder || row.HasChildren) && _collapsed.Contains(selected.Id)) ToggleBranch(selected);
                else if (keyData == Keys.Left && selected.ParentId is { } parent)
                {
                    for (int index = 0; index < _items.Items.Count; index++)
                        if (((Bookmark)_items.Items[index]).Id == parent) { SelectOnly(index); break; }
                }
                else if (keyData == Keys.Right && row.HasChildren && _items.SelectedIndex + 1 < _items.Items.Count)
                    SelectOnly(_items.SelectedIndex + 1);
            }
            return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
    }
    private void DrawBookmark(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        Bookmark bookmark = (Bookmark)_items.Items[e.Index];
        bool selected = _selectedIds.Contains(bookmark.Id);
        bool insertionTarget = _dropInsertion && _dropTargetId == bookmark.Id;
        bool dropTarget = !_dropInsertion && _dropTargetId == bookmark.Id;
        using var background = new SolidBrush(dropTarget ? Color.FromArgb(199, 231, 217) : selected ? Color.FromArgb(220, 238, 232) : BookmarkColors.ListPaper(bookmark.Color));
        e.Graphics.FillRectangle(background, e.Bounds);
        if (insertionTarget)
        {
            using var insertion = new Pen(UiStyle.Accent, Scale(3));
            int y = _dropAfter ? e.Bounds.Bottom - Scale(2) : e.Bounds.Top + Scale(1);
            e.Graphics.DrawLine(insertion, e.Bounds.Left + Scale(8), y, e.Bounds.Right - Scale(8), y);
        }
        if (bookmark.Color != BookmarkColor.Default)
        {
            using var swatch = new SolidBrush(BookmarkColors.Get(bookmark.Color).Swatch);
            e.Graphics.FillRectangle(swatch, e.Bounds.Left + Scale(1), e.Bounds.Top + Scale(8), Scale(3), e.Bounds.Height - Scale(16));
        }
        _rows.TryGetValue(bookmark.Id, out var row);
        int depth = row?.Depth ?? 0;
        int indent = Scale(28 + Math.Min(depth, 14) * 20);
        var top = new Rectangle(e.Bounds.X + indent, e.Bounds.Y + Scale(8), e.Bounds.Width - indent - Scale(10), Scale(24));
        if (bookmark.IsFolder || row?.HasChildren == true)
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
        int titleInset = 0;
        if (bookmark.IsFolder)
        {
            titleInset = Scale(25);
            using var paper = new SolidBrush(Color.FromArgb(244, 209, 126));
            using var outline = new Pen(Color.FromArgb(158, 116, 43));
            var icon = new Rectangle(top.X + Scale(2), top.Y + Scale(7), Scale(18), Scale(13));
            e.Graphics.FillRectangle(paper, icon.X, icon.Y - Scale(4), Scale(8), Scale(5));
            e.Graphics.FillRectangle(paper, icon);
            e.Graphics.DrawRectangle(outline, icon);
        }
        TextRenderer.DrawText(e.Graphics, title, Font, new Rectangle(top.X + titleInset, top.Y, top.Width - titleInset - Scale(106), top.Height), UiStyle.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, date, Font, new Rectangle(top.Right - Scale(102), top.Y, Scale(102), top.Height), UiStyle.Muted, TextFormatFlags.Right | TextFormatFlags.NoPrefix);
        string location = bookmark.IsFolder
            ? $"책갈피 폴더 · {_bookmarks.Count(item => item.ParentId == bookmark.Id)}개 항목 · {(bookmark.FolderExpanded ? "열림" : "닫힘")}" : UiStyle.Location(bookmark);
        TextRenderer.DrawText(e.Graphics, location, Font, new Rectangle(top.X, top.Y + Scale(27), top.Width, Scale(23)), UiStyle.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, bookmark.IsFolder ? "클릭하여 열기/닫기 · 항목을 끌어 넣을 수 있습니다." : bookmark.Note,
            Font, new Rectangle(top.X, top.Y + Scale(52), top.Width, Scale(23)), UiStyle.Accent, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
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
        if (disposing && _isDisposing) return;
        if (disposing)
        {
            _isDisposing = true;
            ++_loadVersion;
            _searchTimer.Dispose();
            _dragScrollTimer.Dispose();
            // WinForms 10.0.12 disconnects cached UIA children while enumerating
            // their dictionary. A reentrant GetChild can add to that dictionary.
            // Clear() removes logical items before disconnecting their providers,
            // so teardown queries return no children instead of growing the cache.
            // Keep ordinary accessibility and the framework's provider cleanup.
            _items.Items.Clear();
            _menu.Dispose();
            _tooltip.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class SelectionListBox : ListBox
    {
        internal bool PointerDispatch { get; private set; }
        protected override void WndProc(ref Message message)
        {
            bool wasPointer = PointerDispatch;
            // Selection notifications can occur on either press or release.
            // Keep native focus changes from replacing our Ctrl selection set.
            if (message.Msg is 0x0200 or 0x0201 or 0x0202 or 0x0203) PointerDispatch = true;
            try { base.WndProc(ref message); }
            finally { PointerDispatch = wasPointer; }
        }
    }
}
