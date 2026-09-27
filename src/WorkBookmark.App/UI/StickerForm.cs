using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WorkBookmark.Core;

namespace WorkBookmark.App.UI;

/// <summary>A bookmark view; all data changes go through the application context.</summary>
internal sealed class StickerForm : Form
{
    public static readonly Size DefaultClientSize = new(260, 170);
    public static readonly Size MinimumClientSize = new(240, 160);

    private static readonly Color Paper = Color.FromArgb(255, 253, 245);
    private static readonly Color Rule = Color.FromArgb(226, 225, 213);
    private static readonly Color Hover = Color.FromArgb(241, 239, 228);
    private readonly Panel _header = new();
    private readonly PictureBox _typeIcon = new();
    private readonly Label _kind = new();
    private readonly Button _collapse = new();
    private readonly Button _options = new();
    private readonly TitleButton _title = new();
    private readonly Label _resumeStatus = new();
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
    public Size DefaultExpandedSize => SizeFromClientSize(new Size(Px(DefaultClientSize.Width), Px(DefaultClientSize.Height)));
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
        _bodyFont = new Font("맑은 고딕", 9F);
        Font = _bodyFont;
        _titleFont = new Font(Font.FontFamily, 10.5F, FontStyle.Bold);
        _smallFont = new Font(Font.FontFamily, 8.5F);
        _noteFont = new Font(Font.FontFamily, 9.5F);
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
        ClientSize = DefaultClientSize;

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

        ConfigureButton(_title, "", "책갈피 제목 · 메모 편집", 1);
        _title.Font = _titleFont;
        _title.ForeColor = UiStyle.Ink;
        _title.AutoEllipsis = true;
        _title.TextAlign = ContentAlignment.TopLeft;
        _title.Padding = Padding.Empty;
        _title.Cursor = Cursors.IBeam;
        _title.Click += (_, _) => RequestNoteEdit();
        _resumeStatus.Font = _smallFont;
        _resumeStatus.ForeColor = Color.FromArgb(138, 80, 43);
        _resumeStatus.AutoEllipsis = true;
        _resumeStatus.UseMnemonic = false;
        _resumeStatus.AccessibleName = "이어가기 상태";

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
        Controls.AddRange([_header, _title, _resumeStatus, _noteEditor, _noteStatus, _cancelNote, _delete, _resume]);
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
        string title = PresentationTitle(value);
        Text = $"{title} · 업무 책갈피";
        AccessibleName = $"책갈피 스티커: {title}";
        _kind.Text = IsCollapsed ? title : KindLabel(value.Target.Kind);
        UpdateTypeIcon();
        _title.Text = title;
        string? issue = value.LastResumeResult switch
        {
            ResultCode.TargetUnavailable => "대상에 접근할 수 없습니다. 다시 확인해 주세요.",
            ResultCode.OpenedPositionFailed => "파일은 열렸지만 작업 위치로 이동하지 못했습니다.",
            ResultCode.ResumeOutcomeUnknown => "처리 결과를 확인하지 못했습니다. 대상 앱을 확인해 주세요.",
            ResultCode.AppBusy => "편집이나 대화상자를 마친 뒤 다시 시도해 주세요.",
            _ => null
        };
        _resumeStatus.Text = issue ?? "";
        bool empty = string.IsNullOrWhiteSpace(value.Note);
        string details = title + "\n" + value.DisplayName + "\n" + UiStyle.Location(value) + $"\n저장: {value.CapturedAtUtc.ToLocalTime():yyyy.MM.dd HH:mm}";
        if (value.Target.HadUnsavedChanges == true) details += "\n저장 당시 문서에 미저장 변경이 있었습니다.";
        if (value.LastResumeResult == ResultCode.TargetUnavailable)
            details += CanRelink(value) ? "\n더 보기에서 위치를 다시 지정할 수 있습니다." : "\n대상 앱과 저장한 주소를 확인한 뒤 다시 시도해 주세요.";
        _tooltip.SetToolTip(_title, details + "\n클릭하여 메모 편집 (Ctrl+E)");
        _tooltip.SetToolTip(_kind, details + "\n드래그하여 이동");
        _tooltip.SetToolTip(_resumeStatus, issue is null ? "" : issue + "\n" + details);
        _title.AccessibleDescription = details + (empty ? "\n메모가 없습니다. 클릭하거나 Ctrl+E로 추가할 수 있습니다." : "\n클릭하거나 Ctrl+E로 메모를 편집할 수 있습니다.");
        _resumeStatus.AccessibleDescription = issue;
        RefreshActionState();
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
            _noteStatus.Text = "다른 창으로 이동하면 자동저장\nEnter 저장 · Esc 취소";
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
                else if (!IsEditingNote && ContainsFocus) _title.Focus();
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
        _title.Focus();
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
        _resumeStatus.Visible = expanded && !editing && _resumeStatus.Text.Length > 0;
        foreach (Control control in new Control[] { _title, _delete, _resume })
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
            _kind.Text = collapsed ? PresentationTitle(Bookmark) : KindLabel(Bookmark.Target.Kind);
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
        MinimumSize = SizeFromClientSize(new Size(Px(MinimumClientSize.Width), IsCollapsed ? Px(36) : Px(MinimumClientSize.Height)));
        // A zero width with a nonzero maximum height is a real width limit in
        // WinForms; it would also shrink MinimumSize and collapse the note to 42px.
        MaximumSize = IsCollapsed
            ? new Size(Math.Max(MinimumSize.Width, SystemInformation.MaxWindowTrackSize.Width), CollapsedHeight)
            : Size.Empty;
    }

    private int CollapsedHeight => SizeFromClientSize(new Size(Px(MinimumClientSize.Width), Px(36))).Height;
    private int Px(int value) => (int)Math.Round(value * DeviceDpi / 96F);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (!_layoutReady) return;
        int gap = Px(12), width = ClientSize.Width;
        _header.SetBounds(0, 0, width, Px(36));
        _options.SetBounds(width - gap - Px(28), Px(3), Px(28), Px(30));
        _collapse.SetBounds(_options.Left - Px(48), Px(3), Px(46), Px(30));
        _typeIcon.SetBounds(gap, Px(8), Px(20), Px(20));
        _kind.SetBounds(_typeIcon.Right + Px(6), Px(3), Math.Max(0, _collapse.Left - _typeIcon.Right - Px(10)), Px(30));
        int bottom = ClientSize.Height - gap;
        _resume.SetBounds(width - gap - Px(84), bottom - Px(30), Px(84), Px(30));
        _delete.SetBounds(gap - Px(7), bottom - Px(30), Px(60), Px(30));
        _cancelNote.Bounds = _delete.Bounds;
        // A fixed two-line title keeps long notes and fallback URLs from growing
        // the card. The tooltip and inline editor retain the complete contents.
        _title.SetBounds(gap - Px(3), Px(40), width - gap * 2 + Px(6), _title.Font.Height * 2 + Px(2));
        int statusTop = _title.Bottom + Px(2);
        _resumeStatus.SetBounds(gap, statusTop, width - gap * 2, Math.Max(0, _resume.Top - Px(3) - statusTop));
        _noteEditor.SetBounds(gap, Px(44), width - gap * 2, _noteEditor.PreferredHeight);
        int noteStatusTop = _noteEditor.Bottom + Px(6);
        _noteStatus.SetBounds(gap, noteStatusTop, width - gap * 2, Math.Max(0, _cancelNote.Top - Px(4) - noteStatusTop));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Rule);
        e.Graphics.DrawLine(pen, Px(12), Px(35), ClientSize.Width - Px(12), Px(35));
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
        if (_title.Focused && (keyData == Keys.Enter || keyData == Keys.Space)) { RequestNoteEdit(); return true; }
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
        int size = Px(20);
        if (_iconIdentity == identity && _iconSize == size) return;
        _iconIdentity = identity;
        _iconSize = size;
        int revision = ++_iconRevision;
        _iconLookupStarted = false;
        Image? previous = _typeIcon.Image;
        _typeIcon.Image = BookmarkTypeIcon.Create(Bookmark.Target, size);
        previous?.Dispose();
        _typeIcon.AccessibleDescription = KindLabel(Bookmark.Target.Kind);
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

    private static string PresentationTitle(Bookmark bookmark)
    {
        if (!string.IsNullOrWhiteSpace(bookmark.Note)) return bookmark.Note.Trim();
        CapturedTarget target = bookmark.Target;
        if (target.Kind == TargetKind.NotepadSnapshot) return bookmark.DisplayName;
        if (target.Kind is TargetKind.WebPage or TargetKind.Folder || OfficeLocation.IsWebTarget(target)) return target.Path;
        string name = Path.GetFileName(target.Path);
        return string.IsNullOrWhiteSpace(name) ? bookmark.DisplayName : name;
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

    // Button semantics keep the title reachable by Tab, Enter/Space and assistive
    // technology, while explicit text drawing limits the visible title to two lines.
    private sealed class TitleButton : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            int padding = (int)Math.Round(3 * DeviceDpi / 96F);
            var textBounds = new Rectangle(padding, 0, Math.Max(0, Width - padding * 2), Font.Height * 2);
            TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, ForeColor,
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle, ForeColor, BackColor);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
