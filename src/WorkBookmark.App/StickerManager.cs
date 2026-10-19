using System.Drawing;
using System.Runtime.InteropServices;
using WorkBookmark.App.UI;
using WorkBookmark.Core;

namespace WorkBookmark.App;

/// <summary>Owns presentations of the same bookmark identities; closing a view never deletes data.</summary>
internal sealed class StickerManager : IDisposable
{
    private readonly Func<Task<(IReadOnlyList<Bookmark> Items, IReadOnlyList<StickerLayout> Layouts)>> _load;
    private readonly Action<IReadOnlyList<StickerLayout>> _save;
    private readonly Action<string> _notify;
    private readonly Dictionary<Guid, StickerForm> _forms = [];
    private readonly Dictionary<Guid, StickerLayout> _layouts = [];
    private readonly Dictionary<Guid, StickerLayout> _pending = [];
    private readonly HashSet<Guid> _unsaved = [];
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 350 };
    private Task? _writeTask;
    private StickerLayout[] _writingBatch = [];
    private int _loadVersion, _revision;
    private bool _enabled, _hidden, _disposed, _applying, _showPending;
    private Guid? _busyBookmarkId;
    private bool _refreshingHierarchy;
    private StickerForm? _movingForm, _dropTarget;
    private Point _moveOrigin;
    private readonly Dictionary<Guid, Point> _subtreeMoveOrigins = [];
    private readonly HashSet<Guid> _layoutOnShow = [];
    private readonly HashSet<Guid> _folderHidden = [], _newViews = [];
    private readonly Dictionary<Guid, Guid> _folderOrigins = [];

    internal event Action<Bookmark>? ResumeRequested, NoteRequested, DeleteRequested, RelinkRequested;
    internal event Action<Bookmark, Guid?>? ParentChangeRequested;
    internal event Action<Bookmark, BookmarkColor>? ColorChangeRequested;
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
        _saveTimer.Tick += async (_, _) => await SavePendingAsync();
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
        if (resized.Length > 0) await Task.Run(() => _save(resized));
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
        _enabled = enabled;
        if (!enabled) { ++_loadVersion; HideAll(); return; }
        await ShowAllAsync(activate);
    }

    internal Task ToggleVisibilityAsync()
    {
        if (!_enabled || _disposed) return Task.CompletedTask;
        // Use actual visibility: individual hides and note editing can differ
        // from the hide-all policy. A second press also cancels a pending reveal.
        if (_showPending || _forms.Values.Any(form => form.Visible))
        {
            HideAll();
            return Task.CompletedTask;
        }
        return ShowAllAsync();
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
            foreach (var layout in loaded.Layouts) _layouts.TryAdd(layout.BookmarkId, layout);
            foreach (var (folderId, targetId) in _folderOrigins)
                if (!_layouts.ContainsKey(folderId) && _layouts.TryGetValue(targetId, out var origin))
                    RememberFolderOrigin(folderId, origin, loaded.Items.FirstOrDefault(item => item.Id == folderId)?.ParentId);
            var active = loaded.Items.Where(item => item.DeletedAtUtc is null).ToArray();
            foreach (var item in active)
                // Inspect the saved topology before Upsert remembers any newly
                // constructed form. A list-only edit can precede this process.
                if ((_layouts.TryGetValue(item.Id, out var saved) ? saved.ParentId != item.ParentId : item.ParentId is not null) ||
                    _forms.TryGetValue(item.Id, out var previous) && previous.Bookmark.ParentId != item.ParentId)
                    _layoutOnShow.Add(item.Id);
            var identities = active.Select(item => item.Id).ToHashSet();
            _refreshingHierarchy = true;
            try
            {
                foreach (var id in _forms.Keys.Where(id => !identities.Contains(id)).ToArray()) Remove(id);
                foreach (var item in active.Reverse()) Upsert(item);
            }
            finally { _refreshingHierarchy = false; }
            UpdateHierarchy();
            foreach (Guid id in _layoutOnShow.ToArray())
            {
                LayoutGroup(id);
                if (_forms.TryGetValue(id, out var changed)) Remember(changed);
            }
            _layoutOnShow.Clear();
            ApplyFolderVisibility(revealAll: true);
            foreach (var item in active.Reverse())
            {
                var form = _forms[item.Id];
                if (!form.Visible) continue;
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
                    Remember(existing);
                }
                ApplyFolderVisibility();
            }
            return;
        }
        // ShowAll compares saved parent snapshots with the authoritative graph.
        // A color or note update in list mode must not queue an unchanged group
        // for rearrangement merely because its sticker has not been created yet.
        if (!_enabled) return;
        var form = new StickerForm(item);
        form.AdjustMoveBounds = (bounds, modifiers) => MoveSticker(form, bounds, modifiers);
        form.NativeMoveCompleted += CompleteMove;
        form.DetachRequested += value => ParentChangeRequested?.Invoke(value, null);
        form.ColorChangeRequested += (value, color) => ColorChangeRequested?.Invoke(value, color);
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
        form.PlacementChanged += Remember;
        _forms.Add(item.Id, form);
        _newViews.Add(item.Id);
        _applying = true;
        try
        {
            if (_layouts.TryGetValue(item.Id, out var layout))
            {
                Screen screen = Screen.AllScreens.FirstOrDefault(candidate => candidate.DeviceName == layout.MonitorDevice) ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
                form.Location = screen.WorkingArea.Location;
                _ = form.Handle;
                form.Bounds = RestoreBounds(layout, screen.WorkingArea, form.DeviceDpi, form.MinimumSize);
                // Sticker mode is always above ordinary windows, including layouts
                // saved by older releases before that became the display contract.
                form.ApplyPresentation(layout.IsCollapsed, true);
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
        Remember(form);
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
            foreach (var folder in changes.Where(item => item.IsFolder && !_forms.ContainsKey(item.Id) &&
                         changes.Any(child => child.Id == target && child.ParentId == item.Id)))
            {
                _folderOrigins[folder.Id] = target;
                if (_forms.TryGetValue(target, out var targetForm)) Remember(targetForm);
                if (_layouts.TryGetValue(target, out var origin))
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
            if (_forms.TryGetValue(id, out var form)) Remember(form);
        }
        ApplyFolderVisibility();
    }

    private void RememberFolderOrigin(Guid folderId, StickerLayout origin, Guid? parentId)
    {
        var layout = origin with { BookmarkId = folderId, ParentId = parentId };
        _layouts[folderId] = _pending[folderId] = layout;
        _unsaved.Add(folderId);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    internal void Remove(Guid id)
    {
        ++_revision;
        if (!_forms.Remove(id, out var form)) return;
        _folderHidden.Remove(id);
        _newViews.Remove(id);
        _folderOrigins.Remove(id);
        if (ReferenceEquals(_movingForm, form)) ClearMove();
        if (ReferenceEquals(_dropTarget, form)) SetDropTarget(null);
        Remember(form);
        form.Dispose();
        if (!_refreshingHierarchy) { UpdateHierarchy(); ApplyFolderVisibility(); }
    }

    internal void HideAll()
    {
        ++HideVersion;
        _hidden = true;
        _showPending = false;
        ++_loadVersion;
        ClearMove();
        foreach (var form in _forms.Values) form.Hide();
    }

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
        EnsureOnScreen(form);
        // Editing reveals only its sticker, preserving all other hidden views.
        form.Show();
        form.Activate();
        return form;
    }

    private HashSet<Guid> FolderVisibleIds()
    {
        var closed = _forms.Values.Where(form => form.Bookmark.IsFolder && !form.Bookmark.FolderExpanded)
            .Select(form => form.Bookmark.Id).ToHashSet();
        return BookmarkTree.Flatten(_forms.Values.Select(form => form.Bookmark), closed).Select(row => row.Bookmark.Id).ToHashSet();
    }

    private void ApplyFolderVisibility(bool revealAll = false)
    {
        var visible = FolderVisibleIds();
        foreach (var form in _forms.Values)
        {
            Guid id = form.Bookmark.Id;
            bool created = _newViews.Remove(id);
            if (!visible.Contains(id))
            {
                // Remember only windows hidden by the folder. A user-hidden child
                // should not reappear merely because its ancestor was reopened.
                if (form.Visible || created || revealAll) _folderHidden.Add(id);
                form.HideForFolder();
                continue;
            }
            bool restore = _folderHidden.Remove(id);
            if (_enabled && !_hidden && (revealAll || created || restore)) form.Show();
        }
    }

    private Rectangle SnapMove(StickerForm moving, Rectangle proposed, Keys modifiers)
    {
        if (!SnapEnabled || !_enabled || _disposed || _applying || (modifiers & Keys.Alt) != 0) return proposed;
        var screen = Screen.FromRectangle(proposed);
        var descendants = Subtree(moving.Bookmark.Id).Select(row => row.Bookmark.Id).ToHashSet();
        var neighbors = _forms.Values.Where(form => !descendants.Contains(form.Bookmark.Id) && !form.IsDisposed && form.Visible &&
                Screen.FromControl(form).DeviceName == screen.DeviceName)
            .Select(form => form.Bounds).ToArray();
        return StickerSnap.Snap(proposed, neighbors, screen.WorkingArea, moving.DeviceDpi);
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

    private Rectangle MoveSticker(StickerForm moving, Rectangle proposed, Keys modifiers)
    {
        if (!_enabled || _disposed || _applying) return proposed;
        if (!ReferenceEquals(_movingForm, moving))
        {
            ClearMove();
            _movingForm = moving;
            _moveOrigin = moving.Location;
            foreach (var row in Subtree(moving.Bookmark.Id).Skip(1))
                _subtreeMoveOrigins[row.Bookmark.Id] = _forms[row.Bookmark.Id].Location;
        }
        SetDropTarget(FindDropTarget(moving, Cursor.Position, modifiers));
        Rectangle result = _dropTarget is not null ? proposed : SnapMove(moving, proposed, modifiers);
        MoveDescendants(result.Location);
        return result;
    }

    private StickerForm? FindDropTarget(StickerForm moving, Point cursor, Keys modifiers)
    {
        // Placement and hierarchy changes are separate gestures in either snap
        // mode. Overlapping a card while moving must never silently group it.
        if ((modifiers & Keys.Shift) == 0 || (modifiers & Keys.Alt) != 0 ||
            moving.IsEditingNote || moving.Bookmark.Id == _busyBookmarkId) return null;
        var bookmarks = _forms.Values.Select(form => form.Bookmark).ToArray();
        var candidates = _forms.Values.Where(form => form != moving && form.Visible && !form.IsDisposed &&
            !form.IsEditingNote && form.Bookmark.Id != _busyBookmarkId && form.Bounds.Contains(cursor) &&
            form.Bookmark.Id != moving.Bookmark.ParentId &&
            BookmarkTree.CanDropOnto(bookmarks, moving.Bookmark.Id, form.Bookmark.Id)).ToArray();
        if (candidates.Length < 2) return candidates.FirstOrDefault();
        // Native z-order decides which of several overlapping stickers receives the drop.
        var byWindow = candidates.ToDictionary(form => form.Handle);
        for (nint window = GetTopWindow(nint.Zero); window != nint.Zero; window = GetWindow(window, 2))
            if (byWindow.TryGetValue(window, out var candidate)) return candidate;
        return null;
    }

    private void SetDropTarget(StickerForm? target)
    {
        if (ReferenceEquals(_dropTarget, target)) return;
        if (_dropTarget is { IsDisposed: false }) _dropTarget.SetDropTarget(false);
        _dropTarget = target;
        if (_dropTarget is not null) _dropTarget.SetDropTarget(true);
    }

    private void MoveDescendants(Point location)
    {
        var delta = new Size(location.X - _moveOrigin.X, location.Y - _moveOrigin.Y);
        foreach (var (id, origin) in _subtreeMoveOrigins)
            if (_forms.TryGetValue(id, out var form)) form.Location = origin + delta;
    }

    private void CompleteMove(StickerForm moving, bool moved) =>
        FinishMove(moving, moved, Cursor.Position, Control.ModifierKeys);

    private void FinishMove(StickerForm moving, bool moved, Point cursor, Keys modifiers)
    {
        if (!ReferenceEquals(_movingForm, moving)) return;
        // Windows restores the source to its initial location on Esc. Follow its
        // final position so descendants also roll back without a hierarchy write.
        MoveDescendants(moving.Location);
        // Individual note/list reveals can be visible while hide-all remains
        // the policy for other stickers. Actual visibility decides drop eligibility.
        // Commit only the target already highlighted during this gesture, and
        // revalidate it at release. A late modifier or cursor change cannot
        // create an unpreviewed folder, while releasing Shift cancels grouping.
        StickerForm? target = moved && _enabled && moving.Visible && _dropTarget is not null &&
            ReferenceEquals(_dropTarget, FindDropTarget(moving, cursor, modifiers)) ? _dropTarget : null;
        var changed = _subtreeMoveOrigins.Keys.Append(moving.Bookmark.Id).ToArray();
        ClearMove();
        foreach (Guid id in changed)
            if (_forms.TryGetValue(id, out var form)) Remember(form);
        if (target is not null) ParentChangeRequested?.Invoke(moving.Bookmark, target.Bookmark.Id);
    }

    private void ClearMove()
    {
        SetDropTarget(null);
        _movingForm = null;
        _subtreeMoveOrigins.Clear();
    }

    private void LayoutGroup(Guid changedId)
    {
        var rows = Tree();
        int changed = -1;
        for (int index = 0; index < rows.Count; index++)
            if (rows[index].Bookmark.Id == changedId) { changed = index; break; }
        if (changed < 0) return;
        int start = changed;
        while (start > 0 && rows[start].Depth > 0) start--;
        int end = start + 1;
        while (end < rows.Count && rows[end].Depth > 0) end++;
        var group = rows.Skip(start).Take(end - start).ToArray();
        if (group.Length < 2) return;
        var root = _forms[group[0].Bookmark.Id];
        var bounds = StickerTreeLayout.Arrange(group.Select(row => (_forms[row.Bookmark.Id].Size, row.Depth)).ToArray(),
            root.Location, Screen.FromControl(root).WorkingArea, root.DeviceDpi);
        for (int index = 0; index < group.Length; index++)
        {
            var form = _forms[group[index].Bookmark.Id];
            form.SetArrangedLocation(bounds[index].Location);
            Remember(form);
        }
    }

    internal async Task ArrangeAsync(StickerArrangementMode mode = StickerArrangementMode.Grid)
    {
        if (!await ShowAllAsync(false) || _disposed || !_enabled || _hidden) return;
        var screen = Screen.FromPoint(Cursor.Position);
        var rows = Tree();
        var forms = rows.Select(row => _forms[row.Bookmark.Id]).ToArray();
        if (forms.Length == 0) return;
        // Moving to the destination monitor first lets WinForms apply that
        // monitor's DPI before measuring the row/column layout.
        foreach (var form in forms) form.Location = screen.WorkingArea.Location;
        // Flat bookmarks retain the original arrangement exactly. Related items
        // are arranged as units and then placed in indented parent-first rows.
        if (rows.Any(row => row.Depth > 0))
        {
            var groups = new List<BookmarkTreeRow[]>();
            for (int index = 0; index < rows.Count;)
            {
                int end = index + 1;
                while (end < rows.Count && rows[end].Depth > 0) end++;
                groups.Add(rows.Skip(index).Take(end - index).ToArray());
                index = end;
            }
            int indent = (int)Math.Round(20 * forms[0].DeviceDpi / 96d);
            int gap = (int)Math.Round(8 * forms[0].DeviceDpi / 96d);
            var sizes = groups.Select(group => new Size(group.Max(row => _forms[row.Bookmark.Id].Width + row.Depth * indent),
                group.Sum(row => _forms[row.Bookmark.Id].Height) + (group.Length - 1) * gap)).ToArray();
            var anchors = StickerArrangement.Arrange(sizes, screen.WorkingArea, forms[0].DeviceDpi, mode);
            for (int index = 0; index < groups.Count; index++)
            {
                var group = groups[index];
                var grouped = StickerTreeLayout.Arrange(group.Select(row => (_forms[row.Bookmark.Id].Size, row.Depth)).ToArray(),
                    anchors[index].Location, screen.WorkingArea, forms[0].DeviceDpi);
                for (int child = 0; child < group.Length; child++)
                {
                    var form = _forms[group[child].Bookmark.Id];
                    form.SetArrangedLocation(grouped[child].Location);
                    Remember(form);
                }
            }
            return;
        }
        var bounds = StickerArrangement.Arrange(forms.Select(form => form.Size).ToArray(), screen.WorkingArea, forms[0].DeviceDpi, mode);
        for (int index = 0; index < forms.Length; index++)
        {
            forms[index].SetArrangedLocation(bounds[index].Location);
            Remember(forms[index]);
        }
    }

    private void EnsureOnScreen(StickerForm form)
    {
        var screen = Screen.FromRectangle(form.Bounds);
        var corrected = ClampBounds(form.Bounds, screen.WorkingArea, form.MinimumSize);
        if (corrected != form.Bounds) { form.Bounds = corrected; Remember(form); }
    }

    private void Remember(StickerForm form)
    {
        if (_disposed || _applying || form.IsDisposed) return;
        // Editing can temporarily enlarge and move a window to fit the screen.
        // Persist the normal placement even when shutdown happens during editing.
        Rectangle bounds = form.PlacementBounds;
        var screen = Screen.FromRectangle(form.PlacementScreenBounds);
        float scale = 96F / Math.Max(96, form.DeviceDpi);
        var layout = new StickerLayout(form.Bookmark.Id, screen.DeviceName,
            (int)Math.Round((bounds.Left - screen.WorkingArea.Left) * scale),
            (int)Math.Round((bounds.Top - screen.WorkingArea.Top) * scale),
            Math.Max(1, (int)Math.Round(bounds.Width * scale)), Math.Max(1, (int)Math.Round(bounds.Height * scale)),
            form.PlacementIsCollapsed, form.TopMost, form.Bookmark.ParentId);
        if (_layouts.TryGetValue(layout.BookmarkId, out var old) && old == layout) return;
        _layouts[layout.BookmarkId] = layout; _pending[layout.BookmarkId] = layout;
        _unsaved.Add(layout.BookmarkId);
        _saveTimer.Stop(); _saveTimer.Start();
    }

    private async Task SavePendingAsync()
    {
        if (_disposed || _writeTask is not null) return;
        _saveTimer.Stop();
        if (_pending.Count == 0) return;
        var batch = _pending.Values.ToArray(); _pending.Clear();
        _writingBatch = batch;
        var write = Task.Run(() => _save(batch));
        _writeTask = write;
        try
        {
            await write;
            if (!_disposed) MarkSaved(batch);
        }
        catch
        {
            if (_disposed) return;
            foreach (var value in batch) _pending.TryAdd(value.BookmarkId, _layouts[value.BookmarkId]);
            _notify("스티커 배치를 저장하지 못했습니다. 책갈피는 유지됩니다. 다음 이동 또는 종료 때 다시 저장합니다.");
        }
        finally
        {
            if (!_disposed && ReferenceEquals(_writeTask, write)) { _writeTask = null; _writingBatch = []; }
            // Retry on a later user change or graceful shutdown, avoiding repeated failure notifications.
        }
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
