using System.Drawing;
using System.Runtime.InteropServices;
using WorkBookmark.App.UI;
using WorkBookmark.Core;

namespace WorkBookmark.App;

/// <summary>Owns presentations of the same bookmark identities; closing a view never deletes data.</summary>
internal sealed partial class StickerManager : IDisposable
{
    private readonly Func<Task<(IReadOnlyList<Bookmark> Items, IReadOnlyList<StickerLayout> Layouts)>> _load;
    private readonly Action<IReadOnlyList<StickerLayout>> _save;
    private readonly Action<string> _notify;
    private readonly Dictionary<Guid, StickerForm> _forms = [];
    private readonly Dictionary<Guid, StickerLayout> _layouts = [];
    private readonly Dictionary<Guid, StickerLayout> _pending = [];
    private readonly HashSet<Guid> _unsaved = [];
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 350 };
    private Task? _writeTask, _flushTask;
    private StickerLayout[] _writingBatch = [];
    private int _loadVersion, _revision;
    private bool _enabled, _hidden, _disposed, _applying, _showPending;
    private Guid? _busyBookmarkId;
    private bool _refreshingHierarchy;

    internal event Action<Bookmark>? ResumeRequested, NoteRequested, DeleteRequested, RelinkRequested;
    internal event Action<Bookmark, Guid?>? ParentChangeRequested;
    internal event Action<Bookmark, BookmarkColor>? ColorChangeRequested;
    internal event Action<Bookmark, BookmarkSortMode>? SortChildrenRequested;
    internal event Action<Bookmark>? FolderToggleRequested, RenameFolderRequested;
    internal event Action<Guid?>? CreateFolderRequested;
    internal event Action? ShowListRequested, SettingsRequested, UndoRequested;
    internal IReadOnlyCollection<StickerForm> Forms => _forms.Values;
    internal bool SnapEnabled { get; set; } = true;
    internal int HideVersion { get; private set; }

    internal StickerManager(Func<Task<(IReadOnlyList<Bookmark> Items, IReadOnlyList<StickerLayout> Layouts)>> load,
        Action<IReadOnlyList<StickerLayout>> save, Action<string> notify)
    {
        _load = load; _save = save; _notify = notify;
        _saveTimer.Tick += async (_, _) => { try { await SavePendingAsync(); } catch { /* Failure is reported by the writer. */ } };
    }

    internal static Size DefaultExpandedSize96
    {
        get
        {
            // Match StickerForm's non-client styles without constructing a bookmark view
            // or inheriting the monitor DPI of a temporary window.
            using var template = new BoundsTemplate(includeCaption: false);
            return template.OuterSize96(StickerForm.DefaultClientSize);
        }
    }

    internal static Size LegacyDefaultExpandedSize96
    {
        get
        {
            using var template = new BoundsTemplate(includeCaption: true);
            return template.OuterSize96(new Size(260, 170));
        }
    }

    internal static Size CompactDefaultExpandedSize96
    {
        get
        {
            using var template = new BoundsTemplate(includeCaption: false);
            return template.OuterSize96(new Size(260, 88));
        }
    }

    internal async Task ResetSavedSizesAsync(int previousVersion = 0)
    {
        if (_disposed || _enabled) throw new InvalidOperationException("Sticker sizes must be upgraded before displaying them.");
        Size size = DefaultExpandedSize96;
        HashSet<Size> previousDefaults = PreviousDefaultSavedSizes(previousVersion);
        var loaded = await _load();
        if (_disposed) throw new OperationCanceledException();
        var resized = loaded.Layouts.Select(layout =>
        {
            Size saved = new(layout.Width, layout.Height);
            // Compare only with defaults of the recorded presentation version:
            // a v1 custom size may happen to equal the later v2 default. Keeping
            // all other outer sizes unchanged also makes partial-save retries
            // safe after another row has already adopted the newest default.
            bool reset = previousVersion == 0 || previousDefaults.Contains(saved);
            return reset ? layout with { Width = size.Width, Height = size.Height } : layout;
        }).ToArray();
        // GetStickerLayouts includes soft-deleted bookmarks. Their saved sizes change,
        // but loading a layout never restores or displays a deleted bookmark.
        if (resized.Length > 0) await Task.Run(() => { if (_saveInitial is not null) foreach (var layout in resized) _saveInitial(layout); else _save(resized); });
        if (_disposed) throw new OperationCanceledException();
        foreach (var layout in resized) _layouts[layout.BookmarkId] = layout;
    }

    private static HashSet<Size> PreviousDefaultSavedSizes(int previousVersion)
    {
        Size client = previousVersion switch
        {
            1 => new Size(260, 170),
            2 => new Size(260, 88),
            _ => Size.Empty
        };
        HashSet<Size> sizes = [];
        if (client.IsEmpty) return sizes;

        using var template = new BoundsTemplate(includeCaption: previousVersion == 1);
        // Native frame rounding is not linear across DPI. Include the saved
        // defaults for standard and custom Windows scales from 100% to 500%,
        // using exactly the same normalization as Remember.
        for (int dpi = 96; dpi <= 480; ++dpi)
        {
            var scaledClient = new Size((int)Math.Round(client.Width * dpi / 96F),
                (int)Math.Round(client.Height * dpi / 96F));
            Size outer = template.OuterSize(scaledClient, dpi);
            float scale = 96F / Math.Max(96, dpi);
            sizes.Add(new Size(Math.Max(1, (int)Math.Round(outer.Width * scale)),
                Math.Max(1, (int)Math.Round(outer.Height * scale))));
        }
        return sizes;
    }

    internal async Task SetEnabledAsync(bool enabled, bool activate = false)
    {
        if (!enabled && !await HideAllAsync()) return;
        _enabled = enabled;
        if (enabled) await ShowAllAsync(activate);
    }

    internal async Task ToggleVisibilityAsync()
    {
        if (!_enabled || _disposed) return;
        if (_showPending || _forms.Values.Any(form => !form.IsTreeEmbedded && form.Visible)) await HideAllAsync();
        else await ShowAllAsync();
    }

    internal async Task<bool> ShowAllAsync(bool activate = true)
    {
        if (!_enabled || _disposed) return false;
        _hidden = false;
        _showPending = true;
        int version = ++_loadVersion, revision = _revision;
        try
        {
            var loaded = await _load();
            if (_disposed || !_enabled || version != _loadVersion) return false;
            // A capture/delete accepted while the read was in flight must not be overwritten.
            if (revision != _revision) return await ShowAllAsync(activate);
            foreach (var layout in loaded.Layouts) { _layouts.TryAdd(layout.BookmarkId, layout); _committedLayouts.TryAdd(layout.BookmarkId, layout); }
            var active = loaded.Items.Where(item => item.DeletedAtUtc is null).ToArray();
            var identities = active.Select(item => item.Id).ToHashSet();
            _refreshingHierarchy = true;
            try
            {
                foreach (var id in _forms.Keys.Where(id => !identities.Contains(id)).ToArray()) Remove(id);
                foreach (var item in active.Reverse()) Upsert(item);
            }
            finally { _refreshingHierarchy = false; }
            UpdateHierarchy();
            foreach (var root in _forms.Values.Where(form => RootId(form.Bookmark.Id) == form.Bookmark.Id)) Remember(root, automatic: true);
            ApplyFolderVisibility(revealAll: true);
            foreach (var item in active.Reverse())
            {
                var form = _forms[item.Id];
                if (!form.Visible || form.IsTreeEmbedded) continue;
                EnsureOnScreen(form);
                // Explicit reveal raises the windows without activating each one in turn.
                SetWindowPos(form.Handle, form.TopMost ? new nint(-1) : nint.Zero, 0, 0, 0, 0, 0x0013);
            }
            if (activate && active.FirstOrDefault(item => _forms[item.Id].Visible) is { } selected)
            {
                var form = _forms[selected.Id];
                form.Activate();
                form.FocusResume();
            }
            if (activate && active.Length == 0) _notify("책갈피가 없습니다. 작업 창에서 저장 단축키를 눌러 주세요.");
            return true;
        }
        catch
        {
            if (!_disposed && version == _loadVersion)
                _notify("스티커를 불러오지 못했습니다. 저장된 책갈피는 유지됩니다.");
            return false;
        }
        finally { if (version == _loadVersion) _showPending = false; }
    }

    internal void Upsert(Bookmark item)
    {
        if (_disposed) return;
        ++_revision;
        if (item.DeletedAtUtc is not null) { Remove(item.Id); return; }
        if (_forms.TryGetValue(item.Id, out var existing))
        {
            bool parentChanged = existing.Bookmark.ParentId != item.ParentId;
            existing.UpdateBookmark(item);
            if (!_refreshingHierarchy)
            {
                UpdateHierarchy();
                // Parent changes arrive only after storage succeeds. The drag preview
                // never changes the bookmark or moves its prospective parent's group.
                if (parentChanged)
                {
                    LayoutGroup(item.Id);
                }
                ApplyFolderVisibility();
            }
            return;
        }
        // A list-only update must not create a floating presentation.
        if (!_enabled) return;
        var form = new StickerForm(item);
        ConfigureManagedForm(form);
        form.AdjustMoveBounds = (bounds, modifiers) => form.IsTreeEmbedded ? form.Bounds : SnapMove(form, bounds, modifiers);
        form.DetachRequested += RequestDetach;
        form.ColorChangeRequested += (value, color) => ColorChangeRequested?.Invoke(value, color);
        form.SortChildrenRequested += (value, mode) => SortChildrenRequested?.Invoke(value, mode);
        form.FolderToggleRequested += value => FolderToggleRequested?.Invoke(value);
        form.RenameFolderRequested += value => RenameFolderRequested?.Invoke(value);
        form.CreateFolderRequested += parentId => CreateFolderRequested?.Invoke(parentId);
        form.ResumeRequested += value => ResumeRequested?.Invoke(value);
        form.NoteRequested += value => NoteRequested?.Invoke(value);
        form.DeleteRequested += value => DeleteRequested?.Invoke(value);
        form.RelinkRequested += value => RelinkRequested?.Invoke(value);
        form.ShowListRequested += () => ShowListRequested?.Invoke();
        form.SettingsRequested += () => SettingsRequested?.Invoke();
        form.UndoRequested += () => UndoRequested?.Invoke();
        form.HideAllRequested += HideAll;
        form.PlacementChanged += changed =>
        {
            if (_applying || changed.IsTreeEmbedded || RootId(changed.Bookmark.Id) != changed.Bookmark.Id) return;
            _ = PersistPlacedRootAsync(changed);
            RebuildPanels();
        };
        form.FootprintChanged += _ => RebuildPanels();
        form.VisibleChanged += (_, _) =>
        {
            if (!_refreshingHierarchy && !_applying && !_disposed)
                RebuildPanels();
        };
        _forms.Add(item.Id, form);
        _applying = true;
        try
        {
            if (_layouts.TryGetValue(item.Id, out var layout))
            {
                Screen screen = Screen.AllScreens.FirstOrDefault(candidate => candidate.DeviceName == layout.MonitorDevice) ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
                form.Location = screen.WorkingArea.Location;
                _ = form.Handle;
                form.Bounds = RestoreRootBounds(layout, screen.WorkingArea, form.DeviceDpi);
                // Sticker mode is always above ordinary windows, including layouts
                // saved by older releases before that became the display contract.
                form.ApplyPresentation(item.IsFolder ? false : layout.IsCollapsed, true);
            }
            else
            {
                Screen screen = Screen.FromPoint(Cursor.Position);
                form.Location = screen.WorkingArea.Location;
                _ = form.Handle;
                form.Bounds = DefaultBounds(screen.WorkingArea, form.DefaultExpandedSize, _forms.Count - 1, form.MinimumSize);
            }
            form.SetBusy(item.Id == _busyBookmarkId);
        }
        finally { _applying = false; }
        // Also persist automatically assigned initial positions before restart.
        if (!_refreshingHierarchy) Remember(form, automatic: true);
        if (!_refreshingHierarchy)
        {
            UpdateHierarchy();
            if (item.ParentId is not null || _forms.Values.Any(value => value.Bookmark.ParentId == item.Id)) LayoutGroup(item.Id);
            ApplyFolderVisibility();
        }
    }

    /// <summary>Publish one committed folder operation without exposing its intermediate relationships.</summary>
    internal void ApplyFolderChanges(IReadOnlyList<Bookmark> changes, Guid? dropTargetId = null)
    {
        if (_disposed) return;
        var changedParents = changes.Where(item => item.DeletedAtUtc is null &&
            (!_forms.TryGetValue(item.Id, out var old) ? item.ParentId is not null : old.Bookmark.ParentId != item.ParentId))
            .Select(item => item.Id).ToArray();
        if (dropTargetId is Guid target)
        {
            foreach (var folder in changes.Where(item => item.IsFolder && item.ParentId is null && !_forms.ContainsKey(item.Id) &&
                         changes.Any(child => child.Id == target && child.ParentId == item.Id)))
            {
                if (_forms.TryGetValue(target, out var targetForm))
                {
                    // A new root folder keeps the committed target's anchor.
                    if (_hiddenRoots.Contains(RootId(target))) _hiddenRoots.Add(folder.Id);
                    RememberFolderOrigin(folder.Id, CaptureLayout(targetForm), folder.ParentId);
                }
                else if (_layouts.TryGetValue(target, out var origin))
                    RememberFolderOrigin(folder.Id, origin, folder.ParentId);
            }
        }
        _refreshingHierarchy = true;
        try { foreach (var item in changes) Upsert(item); }
        finally { _refreshingHierarchy = false; }
        UpdateHierarchy();
        foreach (Guid id in changedParents)
        {
            LayoutGroup(id);
        }
        ApplyFolderVisibility();
    }

    private void RememberFolderOrigin(Guid folderId, StickerLayout origin, Guid? parentId)
    {
        // The repository owns the group's committed placement; this only seeds its first view.
        _layouts[folderId] = origin with { BookmarkId = folderId, ParentId = parentId, IsCollapsed = false };
    }

    internal void Remove(Guid id)
    {
        ++_revision;
        if (_drag is { } drag && (drag.Source.Bookmark.Id == id || Subtree(id).Any(row => row.Bookmark.Id == drag.Source.Bookmark.Id))) CancelManagedDrag();
        if (!_forms.Remove(id, out var form)) return;
        _pending.Remove(id);
        _unsaved.Remove(id);
        foreach (var panel in _panels.Values) panel.DetachRow(form);
        form.Dispose();
        if (!_refreshingHierarchy) { UpdateHierarchy(); ApplyFolderVisibility(); }
    }

    internal void HideAll() => _ = HideAllAsync();

    internal void SetBusy(Guid? bookmarkId)
    {
        if (_busyBookmarkId == bookmarkId) return;
        Guid? previous = _busyBookmarkId;
        _busyBookmarkId = bookmarkId;
        if (previous is { } oldId && _forms.TryGetValue(oldId, out var oldForm)) oldForm.SetBusy(false);
        if (bookmarkId is { } id && _forms.TryGetValue(id, out var form)) form.SetBusy(true);
    }

    internal bool EditNote(Bookmark bookmark, Func<string, Task> saveNote)
    {
        if (bookmark.IsFolder || PrepareInlineEdit(bookmark) is not { } form) return false;
        form.BeginNoteEdit(saveNote);
        return true;
    }

    internal bool RenameFolder(Bookmark bookmark, Func<string, Task> saveName)
    {
        if (!bookmark.IsFolder || PrepareInlineEdit(bookmark) is not { } form) return false;
        form.BeginFolderRename(saveName);
        return true;
    }

    private StickerForm? PrepareInlineEdit(Bookmark bookmark)
    {
        if (!_enabled || _disposed) return null;
        Upsert(bookmark);
        if (!_forms.TryGetValue(bookmark.Id, out var form) || !FolderVisibleIds().Contains(bookmark.Id)) return null;
        ShowTree(bookmark.Id);
        if (_panels.TryGetValue(RootId(bookmark.Id), out var panel)) panel.EnsureEditorVisible(form);
        if (form.IsTreeEmbedded) form.FocusResume(); else form.Activate();
        return form;
    }

    private HashSet<Guid> FolderVisibleIds()
    {
        var closed = _forms.Values.Where(form => form.Bookmark.IsFolder && !form.Bookmark.FolderExpanded)
            .Select(form => form.Bookmark.Id).ToHashSet();
        return BookmarkTree.Flatten(_forms.Values.Select(form => form.Bookmark), closed).Select(row => row.Bookmark.Id).ToHashSet();
    }

    private void ApplyFolderVisibility(bool revealAll = false) => RebuildPanels(revealAll);

    // The shaped tree host handles card occlusion. Peer coordinates are never changed.
    private Rectangle SnapMove(StickerForm moving, Rectangle proposed, Keys modifiers)
    {
        if (!SnapEnabled || !_enabled || _disposed || _applying || (modifiers & Keys.Alt) != 0) return proposed;
        return StickerSnap.Snap(proposed, RootRectangles(moving.Bookmark.Id).ToArray(), Screen.FromRectangle(proposed).WorkingArea, moving.DeviceDpi);
    }

    private IReadOnlyList<BookmarkTreeRow> Tree() => BookmarkTree.Flatten(_forms.Values.Select(form => form.Bookmark));

    private IReadOnlyList<BookmarkTreeRow> Subtree(Guid id)
    {
        var rows = Tree();
        int start = -1;
        for (int index = 0; index < rows.Count; index++)
            if (rows[index].Bookmark.Id == id) { start = index; break; }
        if (start < 0) return [];
        int end = start + 1;
        while (end < rows.Count && rows[end].Depth > rows[start].Depth) end++;
        return rows.Skip(start).Take(end - start).ToArray();
    }

    private void UpdateHierarchy()
    {
        var children = _forms.Values.Where(form => form.Bookmark.ParentId is not null)
            .GroupBy(form => form.Bookmark.ParentId!.Value).ToDictionary(group => group.Key, group => group.Count());
        foreach (var row in Tree())
        {
            string? parent = row.Depth > 0 && row.Bookmark.ParentId is Guid id && _forms.TryGetValue(id, out var parentForm)
                ? parentForm.Bookmark.IsFolder || string.IsNullOrWhiteSpace(parentForm.Bookmark.Note) ? parentForm.Bookmark.DisplayName : parentForm.Bookmark.Note.Trim()
                : null;
            _forms[row.Bookmark.Id].SetHierarchy(row.Depth, parent, children.GetValueOrDefault(row.Bookmark.Id));
        }
    }

    private void LayoutGroup(Guid changedId) => RebuildPanels();

    internal async Task ArrangeAsync(StickerArrangementMode mode = StickerArrangementMode.Grid)
    {
        if (!await CommitPendingEditsAsync() || !await ShowAllAsync(false) || _disposed || !_enabled) return;
        var screen = Screen.FromPoint(Cursor.Position);
        var roots = Tree().Where(row => row.Depth == 0).Select(row => _forms[row.Bookmark.Id]).ToArray();
        if (roots.Length == 0) return;
        int dpi = MonitorDpi(screen, roots[0].DeviceDpi);
        var sizes = roots.Select(CaptureLayout).Select(layout => new Size(
            (int)Math.Round(layout.Width * dpi / 96d), (int)Math.Round(layout.Height * dpi / 96d))).ToArray();
        var bounds = StickerArrangement.Arrange(sizes, screen.WorkingArea, dpi, mode);
        await CommitLayoutsAsync(roots.Select((form, index) => LayoutAt(form.Bookmark.Id, bounds[index], screen, dpi)).ToArray());
    }

    private void EnsureOnScreen(StickerForm form)
    {
        var screen = Screen.FromRectangle(form.Bounds);
        var corrected = ClampBounds(form.Bounds, screen.WorkingArea, form.MinimumSize);
        if (corrected != form.Bounds) { form.Bounds = corrected; Remember(form, automatic: true); }
    }

    private void Remember(StickerForm form, bool automatic = false)
    {
        if (_disposed || _applying || form.IsDisposed || form.IsTreeEmbedded || RootId(form.Bookmark.Id) != form.Bookmark.Id) return;
        var layout = CaptureLayout(form);
        if (_layouts.TryGetValue(layout.BookmarkId, out var old) && old == layout) return;
        _layouts[layout.BookmarkId] = layout; _pending[layout.BookmarkId] = layout;
        _unsaved.Add(layout.BookmarkId);
        if (automatic) _automaticLayouts.Add(layout.BookmarkId); else _automaticLayouts.Remove(layout.BookmarkId);
        _saveTimer.Stop(); _saveTimer.Start();
    }

    private StickerLayout CaptureLayout(StickerForm form)
    {
        // Editing can temporarily enlarge and move a window to fit the screen.
        // Persist the normal placement even when shutdown happens during editing.
        Rectangle bounds = form.PlacementBounds;
        Rectangle screenBounds = form.PlacementScreenBounds;
        var screen = Screen.FromRectangle(screenBounds);
        float scale = 96F / Math.Max(96, form.DeviceDpi);
        return new StickerLayout(form.Bookmark.Id, screen.DeviceName,
            (int)Math.Round((bounds.Left - screen.WorkingArea.Left) * scale),
            (int)Math.Round((bounds.Top - screen.WorkingArea.Top) * scale),
            Math.Max(1, (int)Math.Round(bounds.Width * scale)), Math.Max(1, (int)Math.Round(bounds.Height * scale)),
            form.PlacementIsCollapsed, form.TopMost, form.Bookmark.ParentId);
    }

    internal async Task SavePendingAsync()
    {
        await FlushPendingAsync();
        while (_layoutOperation is { } operation) await operation;
        await FlushPendingAsync();
    }

    private Task FlushPendingAsync()
    {
        if (_disposed || _pending.Count == 0 && _flushTask is null) return Task.CompletedTask;
        if (_flushTask is not null) return _flushTask;
        var flush = DrainPendingAsync();
        _flushTask = flush.IsCompleted ? null : flush;
        return flush;
    }

    private async Task DrainPendingAsync()
    {
        _saveTimer.Stop();
        try
        {
            while (!_disposed && _pending.Count > 0)
            {
                var batch = _pending.Values.ToArray();
                _pending.Clear();
                var automatic = batch.Where(layout => _automaticLayouts.Remove(layout.BookmarkId)).ToArray();
                var gestures = batch.Except(automatic).ToArray();
                _writingBatch = batch;
                var write = Task.Run(() =>
                {
                    if (_saveInitial is not null) foreach (var layout in automatic) _saveInitial(layout);
                    else if (automatic.Length > 0) _save(automatic);
                    if (gestures.Length > 0) _save(gestures);
                });
                _writeTask = write;
                try
                {
                    await write;
                    if (_disposed) return;
                    foreach (var layout in batch) _committedLayouts[layout.BookmarkId] = layout;
                    MarkSaved(batch);
                }
                catch
                {
                    if (_disposed) return;
                    var previous = batch.Where(value => _committedLayouts.ContainsKey(value.BookmarkId))
                        .Select(value => _committedLayouts[value.BookmarkId]).ToArray();
                    ApplySavedLayouts(previous);
                    foreach (var value in automatic)
                    {
                        _pending.TryAdd(value.BookmarkId, value);
                        _automaticLayouts.Add(value.BookmarkId);
                    }
                    _notify("스티커 배치를 저장하지 못했습니다. 이전 위치를 유지합니다.");
                    throw new BookmarkException(ResultCode.PersistenceFailed);
                }
                finally
                {
                    if (ReferenceEquals(_writeTask, write)) { _writeTask = null; _writingBatch = []; }
                }
            }
        }
        finally { _flushTask = null; }
    }

    private void MarkSaved(IEnumerable<StickerLayout> batch)
    {
        foreach (var value in batch)
            if (_layouts.TryGetValue(value.BookmarkId, out var latest) && latest == value) _unsaved.Remove(value.BookmarkId);
    }

    internal static Rectangle RestoreBounds(StickerLayout layout, Rectangle workingArea, int dpi, Size minimum)
    {
        float scale = Math.Max(96, dpi) / 96F;
        return ClampBounds(new Rectangle(workingArea.Left + (int)Math.Round(layout.Left * scale),
            workingArea.Top + (int)Math.Round(layout.Top * scale),
            (int)Math.Round(layout.Width * scale), (int)Math.Round(layout.Height * scale)), workingArea, minimum);
    }

    internal static Rectangle ClampBounds(Rectangle bounds, Rectangle area, Size minimum)
    {
        int width = Math.Clamp(bounds.Width, Math.Min(minimum.Width, area.Width), area.Width);
        int height = Math.Clamp(bounds.Height, Math.Min(minimum.Height, area.Height), area.Height);
        return new(Math.Clamp(bounds.X, area.Left, area.Right - width), Math.Clamp(bounds.Y, area.Top, area.Bottom - height), width, height);
    }

    private static Rectangle DefaultBounds(Rectangle area, Size size, int index, Size minimum)
    {
        int columns = Math.Max(1, (area.Width - 32) / (size.Width + 16));
        int rows = Math.Max(1, (area.Height - 32) / (size.Height + 16));
        int page = index / (columns * rows), slot = index % (columns * rows);
        int left = area.Right - 16 - size.Width - (slot % columns) * (size.Width + 16) - (page % 6) * 20;
        int top = area.Top + 16 + (slot / columns) * (size.Height + 16) + (page % 6) * 20;
        return ClampBounds(new(left, top, size.Width, size.Height), area, minimum);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _saveTimer.Stop();
        foreach (var form in _forms.Values) Remember(form);
        _saveTimer.Stop();
        // Wait only for a local storage task, whose completion never needs the UI thread.
        try { _writeTask?.GetAwaiter().GetResult(); MarkSaved(_writingBatch); } catch { }
        // Track dirty identities independently of the UI continuation: a failed task
        // may not yet have returned its batch to _pending when shutdown starts.
        try { if (_unsaved.Count > 0) _save(_unsaved.Select(id => _layouts[id]).ToArray()); }
        catch { DiagnosticLog.Write("sticker_layout_shutdown", ResultCode.PersistenceFailed); }
        _disposed = true; ++_loadVersion;
        CancelManagedDrag();
        _dragTimer.Dispose();
        foreach (var panel in _panels.Values) { panel.DetachAll(); panel.Dispose(); }
        _panels.Clear();
        _saveTimer.Dispose();
        foreach (var form in _forms.Values) form.Dispose();
        _forms.Clear();
    }

    private sealed class BoundsTemplate : Form
    {
        private readonly bool _includeCaption;

        internal BoundsTemplate(bool includeCaption)
        {
            _includeCaption = includeCaption;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                if (!_includeCaption) parameters.Style &= ~0x00C00000; // WS_CAPTION, matching StickerForm.
                return parameters;
            }
        }

        internal Size OuterSize96(Size client) => OuterSize(client, 96);

        internal Size OuterSize(Size client, int dpi)
        {
            var styles = CreateParams;
            var rectangle = new NativeRectangle { Right = client.Width, Bottom = client.Height };
            if (!AdjustWindowRectExForDpi(ref rectangle, (uint)styles.Style, false, (uint)styles.ExStyle, (uint)dpi))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return new(rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustWindowRectExForDpi(ref NativeRectangle rectangle, uint style,
        [MarshalAs(UnmanagedType.Bool)] bool menu, uint extendedStyle, uint dpi);

    [DllImport("user32.dll")]
    private static extern nint GetTopWindow(nint window);
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
