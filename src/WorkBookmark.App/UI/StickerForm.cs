using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WorkBookmark.Core;

namespace WorkBookmark.App.UI;

/// <summary>A bookmark view; all data changes go through the application context.</summary>
internal sealed class StickerForm : Form
{
    private static readonly Color Paper = Color.FromArgb(255, 253, 245);
    private static readonly Color Rule = Color.FromArgb(226, 225, 213);
    private static readonly Color Hover = Color.FromArgb(241, 239, 228);
    private readonly Panel _header = new();
    private readonly PictureBox _typeIcon = new();
    private readonly Label _kind = new();
    private readonly Button _collapse = new();
    private readonly Button _options = new();
    private readonly Label _title = new();
    private readonly Label _location = new();
    private readonly Label _noteLabel = new();
    private readonly RichTextBox _note = new();
    private readonly ImeTextBox _noteEditor = new();
    private readonly Label _noteStatus = new();
    private readonly Button _cancelNote = new();
    private readonly Button _delete = new();
    private readonly Button _resume = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _collapseMenu;
    private readonly ToolStripMenuItem _relinkMenu;
    private readonly ToolStripMenuItem _copyMenu;
    private readonly ToolStripMenuItem _undoMenu;
    private readonly ToolTip _tooltip = new() { AutoPopDelay = 12000, InitialDelay = 500, ReshowDelay = 100 };
    private readonly Icon _ownedIcon;
    private readonly Font _bodyFont;
    private readonly Font _titleFont;
    private readonly Font _smallFont;
    private readonly Font _noteFont;
    private Size _expandedSize;
    private bool _presentationChanging;
    private bool _busy;
    private bool _layoutReady;
    private bool _noteSaving;
    private bool _noteAutoSavePending;
    private bool _noteAutoSaveQueued;
    private Func<string, Task>? _saveNoteCallback;
    private string? _iconIdentity;
    private int _iconSize;
    private int _iconRevision;
    private bool _iconLookupStarted;

    public Bookmark Bookmark { get; private set; }
    public bool IsCollapsed { get; private set; }
    public bool IsEditingNote => _saveNoteCallback is not null;
    public Size ExpandedSize => IsCollapsed ? new Size(Width, _expandedSize.Height) : Size;
    public event Action<Bookmark>? ResumeRequested;
    public event Action<Bookmark>? NoteRequested;
    public event Action<Bookmark>? DeleteRequested;
    public event Action<Bookmark>? RelinkRequested;
    public event Action? HideAllRequested;
    public event Action? ShowListRequested;
    public event Action? SettingsRequested;
    public event Action? UndoRequested;
    public event Action<StickerForm>? PlacementChanged;

    // Only the act of showing is non-activating. A real click still activates the window,
    // so its buttons, keyboard navigation and native window move/size commands work.
    protected override bool ShowWithoutActivation => true;

    public StickerForm(Bookmark bookmark)
    {
        Bookmark = bookmark;
        SuspendLayout();
        _bodyFont = new Font("맑은 고딕", 10F);
        Font = _bodyFont;
        _titleFont = new Font(Font.FontFamily, 12F, FontStyle.Bold);
        _smallFont = new Font(Font.FontFamily, 9F);
        _noteFont = new Font(Font.FontFamily, 10.5F);
        ForeColor = UiStyle.Ink;
        BackColor = Paper;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        SizeGripStyle = SizeGripStyle.Show;
        DoubleBuffered = true;
        _ownedIcon = Branding.CreateApplicationIcon();
        Icon = _ownedIcon;
        ClientSize = new Size(336, 302);

        _header.BackColor = Paper;
        _header.TabIndex = 4;
        _header.AccessibleName = "스티커 이동 영역";
        _header.Cursor = Cursors.SizeAll;
        _kind.Font = _smallFont;
        _kind.ForeColor = UiStyle.Accent;
        _kind.AutoEllipsis = true;
        _kind.TextAlign = ContentAlignment.MiddleLeft;
        _kind.Cursor = Cursors.SizeAll;
        _kind.UseMnemonic = false;
        _typeIcon.SizeMode = PictureBoxSizeMode.Zoom;
        _typeIcon.AccessibleName = "책갈피 파일 종류 아이콘";
        _typeIcon.Cursor = Cursors.SizeAll;
        _typeIcon.TabStop = false;
        ConfigureButton(_collapse, "접기", "스티커 접기", 4);
        ConfigureButton(_options, "•••", "스티커 더 보기", 5);
        _collapse.Font = _smallFont;
        _options.Font = _smallFont;
        _header.Controls.AddRange([_typeIcon, _kind, _collapse, _options]);
        _header.MouseDown += DragHeader;
        _typeIcon.MouseDown += DragHeader;
        _kind.MouseDown += DragHeader;
        _tooltip.SetToolTip(_header, "드래그하여 이동 · 창 가장자리를 드래그하여 크기 조절");
        _tooltip.SetToolTip(_kind, "드래그하여 이동 · 창 가장자리를 드래그하여 크기 조절");

        _title.Font = _titleFont;
        _title.AutoEllipsis = true;
        _title.UseMnemonic = false;
        _title.AccessibleName = "책갈피 이름";
        _location.Font = _smallFont;
        _location.ForeColor = UiStyle.Muted;
        _location.AutoEllipsis = true;
        _location.UseMnemonic = false;
        _location.AccessibleName = "저장한 작업 위치";
        _noteLabel.Text = "다음에 할 일";
        _noteLabel.Font = _smallFont;
        _noteLabel.ForeColor = UiStyle.Muted;
        _note.Font = _noteFont;
        _note.BorderStyle = BorderStyle.None;
        _note.BackColor = Paper;
        _note.ReadOnly = true;
        _note.DetectUrls = false;
        _note.ScrollBars = RichTextBoxScrollBars.Vertical;
        _note.WordWrap = true;
        _note.TabStop = true;
        _note.TabIndex = 1;
        _note.Cursor = Cursors.IBeam;
        _note.AccessibleName = "책갈피 메모";
        _tooltip.SetToolTip(_note, "클릭하여 메모 편집 (Ctrl+E)");
        _note.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) RequestNoteEdit(); };

        _noteEditor.Font = _noteFont;
        _noteEditor.BackColor = Paper;
        _noteEditor.ForeColor = UiStyle.Ink;
        _noteEditor.BorderStyle = BorderStyle.FixedSingle;
        _noteEditor.Multiline = false;
        _noteEditor.MaxLength = 500;
        _noteEditor.TabIndex = 0;
        _noteEditor.AccessibleName = "스티커 메모 입력";
        _noteEditor.AccessibleDescription = "한 줄, 최대 500자. 다른 창으로 이동하면 자동저장합니다. Enter로 저장, Esc로 취소합니다.";
        _noteEditor.CompositionEnded += QueueNoteAutoSave;
        _noteStatus.Font = _smallFont;
        _noteStatus.ForeColor = UiStyle.Muted;
        _noteStatus.AccessibleName = "메모 저장 상태";
        _noteStatus.UseMnemonic = false;
        ConfigureButton(_cancelNote, "취소", "스티커 메모 편집 취소", 1);
        _cancelNote.Click += (_, _) => CancelNoteEdit();

        ConfigureButton(_delete, "지우기", "스티커와 목록에서 책갈피 지우기", 6);
        _delete.ForeColor = UiStyle.Muted;
        _delete.FlatAppearance.MouseOverBackColor = Color.FromArgb(249, 232, 225);
        _tooltip.SetToolTip(_delete, "스티커와 목록에서 함께 지웁니다. 원본 파일은 유지됩니다. (Ctrl+Delete)");
        ConfigureButton(_resume, "이어가기", "저장한 작업 위치로 이어가기", 0);
        _resume.BackColor = UiStyle.Accent;
        _resume.ForeColor = Color.White;
        _resume.FlatAppearance.MouseOverBackColor = Color.FromArgb(20, 91, 84);
        _resume.FlatAppearance.MouseDownBackColor = Color.FromArgb(16, 78, 72);
        _tooltip.SetToolTip(_resume, "저장한 작업 위치로 이동 (Ctrl+Enter)");
        AcceptButton = _resume;

        _collapseMenu = new ToolStripMenuItem("스티커 접기", null, (_, _) => ToggleCollapsed());
        _copyMenu = new ToolStripMenuItem("전체 경로/URL 복사", null, (_, _) => CopyLocation());
        _relinkMenu = new ToolStripMenuItem("위치 다시 지정", null, (_, _) => { if (!_busy && !IsEditingNote) RelinkRequested?.Invoke(Bookmark); });
        _undoMenu = new ToolStripMenuItem("지우기 되돌리기", null, (_, _) => { if (!IsEditingNote) UndoRequested?.Invoke(); }) { ShortcutKeyDisplayString = "Ctrl+Z" };
        _menu.Items.AddRange([
            _collapseMenu,
            new ToolStripSeparator(),
            new ToolStripMenuItem("목록으로 찾기", null, (_, _) => ShowListRequested?.Invoke()),
            new ToolStripMenuItem("설정…", null, (_, _) => SettingsRequested?.Invoke()),
            new ToolStripMenuItem("스티커 모두 숨기기", null, (_, _) => HideAllRequested?.Invoke()),
            new ToolStripSeparator(),
            _copyMenu,
            _relinkMenu,
            _undoMenu
        ]);
        _menu.Font = Font;
        _menu.Opening += (_, _) => UpdateMenu();
        _menu.Closed += (_, _) => QueueNoteAutoSave();
        _header.ContextMenuStrip = _menu;
        _typeIcon.ContextMenuStrip = _menu;
        _kind.ContextMenuStrip = _menu;
        _options.Click += (_, _) => _menu.Show(_options, new Point(_options.Width, _options.Height), ToolStripDropDownDirection.BelowLeft);
        _collapse.Click += (_, _) => ToggleCollapsed();
        _delete.Click += (_, _) => { if (!_busy && !IsEditingNote) DeleteRequested?.Invoke(Bookmark); };
        _resume.Click += (_, _) => { if (!_busy && !IsEditingNote) ResumeRequested?.Invoke(Bookmark); };
        Controls.AddRange([_header, _title, _location, _noteLabel, _note, _noteEditor, _noteStatus, _cancelNote, _delete, _resume]);
        _layoutReady = true;
        UpdateBookmark(bookmark);
        ApplyPresentation(false, false);
        SetMinimumSize();
        _expandedSize = Size;
        ResumeLayout(false);
        PerformLayout();
    }

    public void UpdateBookmark(Bookmark value)
    {
        Bookmark = value;
        Text = $"{value.DisplayName} · 업무 책갈피";
        AccessibleName = $"책갈피 스티커: {value.DisplayName}";
        _kind.Text = KindLabel(value.Target.Kind);
        UpdateTypeIcon();
        _title.Text = value.DisplayName;
        string? issue = value.LastResumeResult switch
        {
            ResultCode.TargetUnavailable => "대상에 접근할 수 없습니다. 다시 확인해 주세요.",
            ResultCode.OpenedPositionFailed => "파일은 열렸지만 작업 위치로 이동하지 못했습니다.",
            ResultCode.ResumeOutcomeUnknown => "처리 결과를 확인하지 못했습니다. 대상 앱을 확인해 주세요.",
            ResultCode.AppBusy => "편집이나 대화상자를 마친 뒤 다시 시도해 주세요.",
            _ => null
        };
        _location.Text = issue ?? ShortLocation(value.Target);
        _location.ForeColor = issue is null ? UiStyle.Muted : Color.FromArgb(138, 80, 43);
        bool empty = string.IsNullOrWhiteSpace(value.Note);
        _note.Text = empty ? "여기를 눌러 메모를 입력하세요." : value.Note;
        _note.ForeColor = empty ? UiStyle.Muted : UiStyle.Ink;
        string details = UiStyle.Location(value) + $"\n저장: {value.CapturedAtUtc.ToLocalTime():yyyy.MM.dd HH:mm}";
        if (value.Target.HadUnsavedChanges == true) details += "\n저장 당시 문서에 미저장 변경이 있었습니다.";
        if (value.LastResumeResult == ResultCode.TargetUnavailable)
            details += CanRelink(value) ? "\n더 보기에서 위치를 다시 지정할 수 있습니다." : "\n대상 앱과 저장한 주소를 확인한 뒤 다시 시도해 주세요.";
        _tooltip.SetToolTip(_title, details);
        _tooltip.SetToolTip(_location, details);
        _title.AccessibleDescription = details;
        _location.AccessibleDescription = details;
        _note.AccessibleDescription = empty ? "메모가 없습니다. 클릭하거나 Ctrl+E로 추가할 수 있습니다." : "저장된 메모입니다. 클릭하거나 Ctrl+E로 편집할 수 있습니다.";
        // A refresh updates the saved bookmark, never the editor's in-progress draft.
    }

    public void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshActionState();
        _resume.Text = busy ? "처리 중…" : "이어가기";
    }

    public void BeginNoteEdit(Func<string, Task> saveNote)
    {
        ArgumentNullException.ThrowIfNull(saveNote);
        if (IsDisposed || _busy) return;
        if (!IsEditingNote)
        {
            if (IsCollapsed)
            {
                ApplyPresentation(false, true);
                PlacementChanged?.Invoke(this);
            }
            _saveNoteCallback = saveNote;
            _noteAutoSavePending = false;
            _noteEditor.Text = Bookmark.Note;
            _noteEditor.SelectionStart = _noteEditor.TextLength;
            _noteStatus.Text = "다른 창으로 이동하면 자동저장\n한 줄, 최대 500자 · Enter 저장 · Esc 취소";
            _noteStatus.ForeColor = UiStyle.Muted;
            RefreshActionState();
            PerformLayout();
        }
        if (!Visible) Show();
        Activate();
        _noteEditor.Focus();
    }

    private void RequestNoteEdit()
    {
        if (IsEditingNote) _noteEditor.Focus();
        else if (!_busy) NoteRequested?.Invoke(Bookmark);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!IsEditingNote || _noteSaving || IsDisposed || Disposing) return;
        _noteAutoSavePending = true;
        QueueNoteAutoSave();
    }

    protected override void OnActivated(EventArgs e)
    {
        _noteAutoSavePending = false;
        base.OnActivated(e);
    }

    private void QueueNoteAutoSave()
    {
        if (!_noteAutoSavePending || _noteAutoSaveQueued || IsDisposed || Disposing || !IsHandleCreated) return;
        _noteAutoSaveQueued = true;
        // Let native focus and IME messages finish before reading the draft.
        // Moving between controls or opening this sticker's own menu is not a
        // move to another window. CompositionEnded/menu Closed will retry.
        BeginInvoke((Action)(() =>
        {
            _noteAutoSaveQueued = false;
            if (!_noteAutoSavePending || IsDisposed || Disposing || !IsEditingNote || _noteSaving) return;
            if (ContainsFocus) { _noteAutoSavePending = false; return; }
            if (_noteEditor.IsComposing || _menu.Visible) return;
            _noteAutoSavePending = false;
            _ = SaveNoteAsync();
        }));
    }

    private async Task SaveNoteAsync()
    {
        if (!IsEditingNote || _noteSaving || IsDisposed) return;
        _noteAutoSavePending = false;
        _noteSaving = true;
        _noteStatus.Text = "저장 중…";
        _noteStatus.ForeColor = UiStyle.Muted;
        RefreshActionState();
        string text = _noteEditor.Text.Replace('\r', ' ').Replace('\n', ' ');
        Bookmark revisionBeforeSave = Bookmark;
        try
        {
            await _saveNoteCallback!(text);
            if (IsDisposed) return;
            // The callback can publish a newer authoritative revision while this
            // save is awaiting completion. Never replace that row with an older
            // draft; simple callbacks that do not publish still update locally.
            if (ReferenceEquals(Bookmark, revisionBeforeSave)) Bookmark = Bookmark with { Note = text };
            _saveNoteCallback = null;
            UpdateBookmark(Bookmark);
        }
        catch
        {
            if (IsDisposed) return;
            _noteStatus.Text = "저장하지 못했습니다.\n입력은 유지됩니다. Enter로 재시도";
            _noteStatus.ForeColor = Color.FromArgb(138, 80, 43);
        }
        finally
        {
            _noteSaving = false;
            if (!IsDisposed)
            {
                RefreshActionState();
                if (IsEditingNote && ContainsFocus) _noteEditor.Focus();
                else if (!IsEditingNote && ContainsFocus) _note.Focus();
            }
        }
    }

    private void CancelNoteEdit()
    {
        if (!IsEditingNote || _noteSaving) return;
        _noteAutoSavePending = false;
        _saveNoteCallback = null;
        _noteEditor.Clear();
        RefreshActionState();
        _note.Focus();
    }

    private void RefreshActionState()
    {
        bool editing = IsEditingNote;
        bool expanded = !IsCollapsed;
        _resume.Enabled = !_busy && !editing;
        _delete.Enabled = !_busy && !editing;
        _collapse.Enabled = !editing;
        _cancelNote.Enabled = !_noteSaving;
        _noteEditor.ReadOnly = _noteSaving;
        AcceptButton = editing ? null : _resume;
        _title.Visible = expanded;
        _noteLabel.Visible = expanded;
        _noteLabel.Text = editing ? "메모 편집" : "다음에 할 일";
        foreach (Control control in new Control[] { _location, _note, _delete, _resume })
            control.Visible = expanded && !editing;
        foreach (Control control in new Control[] { _noteEditor, _noteStatus, _cancelNote })
            control.Visible = expanded && editing;
    }

    public void FocusResume()
    {
        if (IsEditingNote) _noteEditor.Focus();
        else if (IsCollapsed) _collapse.Focus();
        else _resume.Focus();
    }

    public void ApplyPresentation(bool collapsed, bool alwaysOnTop)
    {
        _presentationChanging = true;
        SuspendLayout();
        try
        {
            // Retain the stored-layout API while sticker mode consistently stays
            // above ordinary windows, including layouts saved by older versions.
            TopMost = true;
            if (IsEditingNote) collapsed = false;
            if (IsCollapsed != collapsed)
            {
                if (collapsed) _expandedSize = Size;
                IsCollapsed = collapsed;
                SetMinimumSize();
                Size = collapsed
                    ? new Size(Width, CollapsedHeight)
                    : new Size(Width, Math.Max(_expandedSize.Height, MinimumSize.Height));
            }
            _collapse.Text = collapsed ? "펼치기" : "접기";
            _collapse.AccessibleName = collapsed ? "스티커 펼치기" : "스티커 접기";
            _tooltip.SetToolTip(_collapse, collapsed ? "스티커 펼치기 (Ctrl+Space)" : "스티커 접기 (Ctrl+Space)");
            RefreshActionState();
            SizeGripStyle = collapsed ? SizeGripStyle.Hide : SizeGripStyle.Show;
        }
        finally
        {
            ResumeLayout(true);
            _presentationChanging = false;
        }
        Invalidate();
    }

    private void ToggleCollapsed()
    {
        if (IsEditingNote) { _noteEditor.Focus(); return; }
        ApplyPresentation(!IsCollapsed, TopMost);
        PlacementChanged?.Invoke(this);
    }

    private void SetMinimumSize()
    {
        MaximumSize = Size.Empty;
        MinimumSize = new Size(Px(280), IsCollapsed ? CollapsedHeight : Px(280) + Height - ClientSize.Height);
        // A zero width with a nonzero maximum height is a real width limit in
        // WinForms; it would also shrink MinimumSize and collapse the note to 42px.
        MaximumSize = IsCollapsed
            ? new Size(Math.Max(MinimumSize.Width, SystemInformation.MaxWindowTrackSize.Width), CollapsedHeight)
            : Size.Empty;
    }

    private int CollapsedHeight => Px(44) + Height - ClientSize.Height;
    private int Px(int value) => (int)Math.Round(value * DeviceDpi / 96F);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (!_layoutReady) return;
        int gap = Px(16), width = ClientSize.Width;
        _header.SetBounds(0, 0, width, Px(44));
        _options.SetBounds(width - gap - Px(30), Px(6), Px(30), Px(30));
        _collapse.SetBounds(_options.Left - Px(58), Px(6), Px(56), Px(30));
        _typeIcon.SetBounds(gap, Px(10), Px(24), Px(24));
        _kind.SetBounds(_typeIcon.Right + Px(8), Px(6), Math.Max(Px(50), _collapse.Left - _typeIcon.Right - Px(12)), Px(30));
        _title.SetBounds(gap, Px(53), width - gap * 2, Px(48));
        _location.SetBounds(gap, Px(105), width - gap * 2, Px(37));
        _noteStatus.Bounds = _location.Bounds;
        _noteLabel.SetBounds(gap, Px(154), width - gap * 2, Px(22));
        int bottom = ClientSize.Height - Px(15);
        _resume.SetBounds(width - gap - Px(104), bottom - Px(36), Px(104), Px(36));
        _delete.SetBounds(gap - Px(7), bottom - Px(36), Px(62), Px(36));
        _cancelNote.Bounds = _delete.Bounds;
        _note.SetBounds(gap, Px(179), width - gap * 2, Math.Max(Px(18), _resume.Top - Px(18) - Px(179)));
        _noteEditor.SetBounds(gap, Px(179), width - gap * 2, _noteEditor.PreferredHeight);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Rule);
        e.Graphics.DrawLine(pen, Px(16), Px(43), ClientSize.Width - Px(16), Px(43));
        if (!IsCollapsed)
            e.Graphics.DrawLine(pen, Px(16), _resume.Top - Px(10), ClientSize.Width - Px(16), _resume.Top - Px(10));
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        if (!_presentationChanging)
        {
            if (!IsCollapsed) _expandedSize = Size;
            PlacementChanged?.Invoke(this);
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        if (IsCollapsed)
            _expandedSize = new Size((int)Math.Round(_expandedSize.Width * (double)e.DeviceDpiNew / e.DeviceDpiOld),
                (int)Math.Round(_expandedSize.Height * (double)e.DeviceDpiNew / e.DeviceDpiOld));
        base.OnDpiChanged(e);
        SetMinimumSize();
        UpdateTypeIcon();
        PerformLayout();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (IsEditingNote)
        {
            if (keyData == Keys.Enter || keyData == (Keys.Control | Keys.Enter))
            {
                if (_noteEditor.IgnoreSubmit) return base.ProcessCmdKey(ref message, keyData);
                _ = SaveNoteAsync();
                return true;
            }
            if (keyData == Keys.Escape)
            {
                if (_noteEditor.IsComposing) return base.ProcessCmdKey(ref message, keyData);
                CancelNoteEdit();
                return true;
            }
            if (keyData == (Keys.Control | Keys.Space)) return true;
            if (keyData == (Keys.Control | Keys.E)) { _noteEditor.Focus(); return true; }
            // Preserve native text editing (Ctrl+Delete/Ctrl+Z) while the draft
            // is active; these keys must never delete or restore a bookmark.
            return base.ProcessCmdKey(ref message, keyData);
        }
        if (keyData == Keys.Escape) { Hide(); return true; }
        if (keyData == (Keys.Control | Keys.Space)) { ToggleCollapsed(); return true; }
        if (keyData == (Keys.Control | Keys.Z)) { UndoRequested?.Invoke(); return true; }
        if (keyData == (Keys.Control | Keys.E)) { RequestNoteEdit(); return true; }
        if (keyData == (Keys.Control | Keys.Enter)) { if (!_busy) ResumeRequested?.Invoke(Bookmark); return true; }
        if (keyData == (Keys.Control | Keys.Delete)) { if (!_busy) DeleteRequested?.Invoke(Bookmark); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }

    private void UpdateMenu()
    {
        _collapseMenu.Text = IsCollapsed ? "스티커 펼치기" : "스티커 접기";
        _collapseMenu.Enabled = !IsEditingNote;
        _copyMenu.Text = Bookmark.Target.Kind == TargetKind.NotepadSnapshot ? "보관 ID 복사" : "전체 경로/URL 복사";
        _relinkMenu.Visible = CanRelink(Bookmark);
        _relinkMenu.Enabled = !_busy && !IsEditingNote;
        _undoMenu.Enabled = !IsEditingNote;
    }

    private void UpdateTypeIcon()
    {
        string identity = BookmarkTypeIcon.GetIdentity(Bookmark.Target);
        int size = Px(24);
        if (_iconIdentity == identity && _iconSize == size) return;
        _iconIdentity = identity;
        _iconSize = size;
        int revision = ++_iconRevision;
        _iconLookupStarted = false;
        Image? previous = _typeIcon.Image;
        _typeIcon.Image = BookmarkTypeIcon.Create(Bookmark.Target, size);
        previous?.Dispose();
        _typeIcon.AccessibleDescription = _kind.Text;
        if (IsHandleCreated)
        {
            _iconLookupStarted = true;
            _ = LoadTypeIconAsync(Bookmark.Target, size, revision);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_layoutReady && !_iconLookupStarted)
        {
            _iconLookupStarted = true;
            _ = LoadTypeIconAsync(Bookmark.Target, _iconSize, _iconRevision);
        }
    }

    private async Task LoadTypeIconAsync(CapturedTarget target, int size, int revision)
    {
        Image? image = null;
        try
        {
            image = await BookmarkTypeIcon.CreateAsync(target, size);
            if (IsDisposed || Disposing || revision != _iconRevision) return;
            Image? previous = _typeIcon.Image;
            _typeIcon.Image = image;
            image = null;
            previous?.Dispose();
        }
        catch
        {
            // The immediate, locally drawn type icon remains usable when the
            // optional Windows association lookup fails during shutdown.
        }
        finally { image?.Dispose(); }
    }

    private void CopyLocation()
    {
        try { Clipboard.SetText(Bookmark.Target.Path); }
        catch (ExternalException) { _tooltip.Show("클립보드에 복사하지 못했습니다. 다시 시도해 주세요.", this, Px(16), Px(50), 3500); }
    }

    private void DragHeader(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (e.Clicks == 2) { ToggleCollapsed(); return; }
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, (nint)2, nint.Zero); // WM_NCLBUTTONDOWN / HTCAPTION
    }

    private static string KindLabel(TargetKind kind) => kind switch
    {
        TargetKind.ExcelCell => "Excel",
        TargetKind.WordPosition => "Word",
        TargetKind.PowerPointSlide => "PowerPoint",
        TargetKind.PdfPage => "PDF",
        TargetKind.WebPage => "웹 페이지",
        TargetKind.Folder => "폴더",
        TargetKind.NotepadSnapshot or TargetKind.NotepadPosition => "메모장",
        _ => "파일"
    };

    private static string ShortLocation(CapturedTarget target)
    {
        if (target.Kind == TargetKind.NotepadSnapshot)
            return UiStyle.PositionLabel(target).Trim(' ', '·') + " · 자동 보관됨";
        string position = UiStyle.PositionLabel(target).Trim(' ', '·');
        return position.Length == 0 ? target.Path : position + "\n" + target.Path;
    }

    private static bool CanRelink(Bookmark bookmark) => bookmark.LastResumeResult == ResultCode.TargetUnavailable &&
        bookmark.Target.Kind is not (TargetKind.WebPage or TargetKind.NotepadSnapshot) && !OfficeLocation.IsWebTarget(bookmark.Target);

    private static void ConfigureButton(Button button, string text, string accessibleName, int tabIndex)
    {
        button.Text = text;
        button.AccessibleName = accessibleName;
        button.TabIndex = tabIndex;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Hover;
        button.FlatAppearance.MouseDownBackColor = Rule;
        button.UseVisualStyleBackColor = false;
        button.BackColor = Paper;
        button.ForeColor = UiStyle.Muted;
        button.Cursor = Cursors.Hand;
        button.UseMnemonic = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _iconRevision++;
            _typeIcon.Image?.Dispose();
            _typeIcon.Image = null;
            _menu.Dispose();
            _tooltip.Dispose();
        }
        base.Dispose(disposing);
        if (disposing)
        {
            _ownedIcon.Dispose();
            _titleFont.Dispose();
            _smallFont.Dispose();
            _noteFont.Dispose();
            _bodyFont.Dispose();
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
