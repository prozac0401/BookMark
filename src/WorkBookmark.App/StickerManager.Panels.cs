using System.Drawing;
using System.Runtime.InteropServices;
using WorkBookmark.App.UI;
using WorkBookmark.Core;

namespace WorkBookmark.App;

internal sealed partial class StickerManager : IMessageFilter
{
    private readonly Dictionary<Guid, StickerTreePanel> _panels = [];
    private readonly HashSet<Guid> _hiddenRoots = [];
    private readonly Dictionary<Guid, Guid> _knownRoots = [];
    private readonly Dictionary<Guid, StickerLayout> _committedLayouts = [];
    private readonly HashSet<Guid> _automaticLayouts = [];
    private readonly HashSet<Guid> _spaceWarnings = [];
    private static readonly Dictionary<int, Size> RootMinimumSizes = [];
    private Action<StickerLayout>? _saveInitial;
    private bool _rebuildingPanels, _layoutCommitting, _publishingCommit;
    private DragSession? _drag;
    private Task<bool>? _layoutOperation;
    private int _layoutGeneration, _dragStartVersion;
    private readonly System.Windows.Forms.Timer _dragTimer = new() { Interval = 25 };

    internal event Action<BookmarkDropRequest>? DropRequested;
    internal bool HasPendingEdits => _forms.Values.Any(form => form.IsEditingNote);
    internal IReadOnlyCollection<StickerTreePanel> Panels => _panels.Values;
    internal void ConfigureInitialLayoutSave(Action<StickerLayout> save) => _saveInitial = save;
    internal void RaiseTree(Guid id) => BringTreeToFront(RootId(id));

    internal void ApplyCommittedFolderChanges(IReadOnlyList<Bookmark> changes, IReadOnlyList<StickerLayout> layouts, Guid? dropTargetId)
    {
        _publishingCommit = true;
        try
        {
            ApplyFolderChanges(changes, dropTargetId);
            ApplySavedLayouts(layouts);
        }
        finally { _publishingCommit = false; }
        RebuildPanels();
    }

    private Guid RootId(Guid id)
    {
        var seen = new HashSet<Guid>();
        while (_forms.TryGetValue(id, out var form) && form.Bookmark.ParentId is Guid parent &&
               _forms.ContainsKey(parent) && seen.Add(id)) id = parent;
        return id;
    }

    internal async Task<bool> CommitPendingEditsAsync(Guid? subtreeId = null)
    {
        var ids = subtreeId is Guid id ? Subtree(id).Select(row => row.Bookmark.Id).ToHashSet() : null;
        foreach (var form in _forms.Values.ToArray())
        {
            if (ids is not null && !ids.Contains(form.Bookmark.Id) || !form.IsEditingNote) continue;
            if (!await form.CommitPendingEditAsync())
            {
                BringTreeToFront(RootId(form.Bookmark.Id));
                if (_panels.TryGetValue(RootId(form.Bookmark.Id), out var panel)) panel.EnsureEditorVisible(form);
                form.FocusResume();
                _notify("편집 내용을 저장하지 못했습니다. 저장하거나 편집을 취소한 뒤 다시 시도해 주세요.");
                return false;
            }
        }
        return true;
    }

    internal async Task<bool> HideTreeAsync(Guid id)
    {
        Guid root = RootId(id);
        if (!await CommitPendingEditsAsync(root) || _disposed) return false;
        ++HideVersion;
        _hiddenRoots.Add(root);
        CancelManagedDrag();
        RebuildPanels();
        return true;
    }

    internal async Task<bool> HideAllAsync()
    {
        if (!await CommitPendingEditsAsync() || _disposed) return false;
        ++HideVersion;
        _hidden = true;
        _showPending = false;
        ++_loadVersion;
        CancelManagedDrag();
        ClearSelection();
        foreach (var id in _forms.Keys.Select(RootId).Distinct()) _hiddenRoots.Add(id);
        RebuildPanels();
        return true;
    }

    internal void ShowTree(Guid id)
    {
        Guid root = RootId(id);
        // Materialize a global hide before revealing one tree.
        if (_hidden)
            foreach (var current in _forms.Keys.Select(RootId).Distinct()) _hiddenRoots.Add(current);
        _hidden = false;
        _hiddenRoots.Remove(root);
        RebuildPanels();
        BringTreeToFront(root);
    }

    internal bool ShowItem(Guid id)
    {
        if (!_enabled || !_forms.TryGetValue(id, out var form) || !FolderVisibleIds().Contains(id)) return false;
        ShowTree(id);
        if (_panels.TryGetValue(RootId(id), out var panel)) panel.EnsureItemVisible(form);
        form.FocusResume();
        return form.Visible;
    }

    internal void ApplySavedLayouts(IReadOnlyList<StickerLayout> layouts)
    {
        bool applying = _applying;
        _applying = true;
        try
        {
            foreach (var layout in layouts)
            {
                _layouts[layout.BookmarkId] = _committedLayouts[layout.BookmarkId] = layout;
                _pending.Remove(layout.BookmarkId);
                _unsaved.Remove(layout.BookmarkId);
                if (!_forms.TryGetValue(layout.BookmarkId, out var form) || RootId(layout.BookmarkId) != layout.BookmarkId) continue;
                if (form.IsTreeEmbedded) form.SetTreeEmbedded(null);
                Screen screen = Screen.AllScreens.FirstOrDefault(value => value.DeviceName == layout.MonitorDevice)
                    ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
                form.Location = screen.WorkingArea.Location;
                form.Bounds = RestoreRootBounds(layout, screen.WorkingArea, form.DeviceDpi);
            }
        }
        finally { _applying = applying; }
        foreach (var panel in _panels.Values) panel.ResetOpeningDirection();
        RebuildPanels();
    }

    private void RebuildPanels(bool revealAll = false)
    {
        if (_disposed || _rebuildingPanels || _refreshingHierarchy || _applying || _publishingCommit) return;
        _rebuildingPanels = true;
        _applying = true;
        try
        {
            var rows = Tree();
            var visible = FolderVisibleIds();
            var roots = rows.Where(row => row.Depth == 0).Select(row => row.Bookmark.Id).ToHashSet();
            if (revealAll) { _hiddenRoots.Clear(); _hidden = false; }
            // A split inherits its containing root's current presentation state.
            foreach (Guid id in roots)
                if (_knownRoots.TryGetValue(id, out Guid previous) && previous != id && _hiddenRoots.Contains(previous))
                    _hiddenRoots.Add(id);
            foreach (var (id, panel) in _panels.ToArray())
                if (!roots.Contains(id)) { panel.DetachAll(); panel.Dispose(); _panels.Remove(id); }
            foreach (Guid id in _forms.Keys)
                if (_forms[id].IsTreeEmbedded && roots.Contains(id)) _forms[id].SetTreeEmbedded(null);

            for (int start = 0; start < rows.Count;)
            {
                int end = start + 1;
                while (end < rows.Count && rows[end].Depth > 0) end++;
                var group = rows.Skip(start).Take(end - start).ToArray();
                Guid rootId = group[0].Bookmark.Id;
                var root = _forms[rootId];
                foreach (var row in group) _knownRoots[row.Bookmark.Id] = rootId;
                bool shown = _enabled && !_hidden && !_hiddenRoots.Contains(rootId);
                var children = group.Skip(1).Where(row => visible.Contains(row.Bookmark.Id))
                    .Select(row => (_forms[row.Bookmark.Id], row.Depth)).ToArray();
                if (!_panels.TryGetValue(rootId, out var panel) && children.Length > 0)
                {
                    panel = new StickerTreePanel();
                    panel.SpaceUnavailable += value => ReportUnavailableSpace(value.Bookmark.Id);
                    _panels.Add(rootId, panel);
                }
                if (shown)
                {
                    if (!root.Visible) root.Show();
                    if (panel is not null)
                    {
                        bool fits = panel.SetRows(root, children, Screen.FromControl(root).WorkingArea);
                        if (fits && children.Length > 0)
                        {
                            if (!panel.Visible) panel.Show(root);
                            foreach (var (child, _) in children) if (!child.Visible) child.Show();
                        }
                        else
                        {
                            panel.Hide();
                            if (children.Length > 0) ReportUnavailableSpace(rootId);
                        }
                        if (fits || children.Length == 0) _spaceWarnings.Remove(rootId);
                    }
                }
                else
                {
                    root.HideForFolder();
                    panel?.Hide();
                }
                foreach (var row in group.Skip(1))
                    if (!shown || !visible.Contains(row.Bookmark.Id)) _forms[row.Bookmark.Id].HideForFolder();
                start = end;
            }
            // Retain deleted identities' session visibility for restore/undo. A
            // live item moved into another tree inherits that destination instead.
            _hiddenRoots.RemoveWhere(id => _forms.ContainsKey(id) && !roots.Contains(id));
        }
        finally { _applying = false; _rebuildingPanels = false; }
        PruneSelection();
        if (_drag is { } activeDrag && !IsDragSelectionCurrent(activeDrag)) CancelManagedDrag();
    }

    private void ReportUnavailableSpace(Guid rootId)
    {
        if (_spaceWarnings.Add(rootId)) _notify("폴더를 펼칠 공간이 부족합니다. 루트를 화면 안쪽으로 옮겨 주세요.");
    }

    private void BringTreeToFront(Guid rootId)
    {
        if (_disposed || !_forms.TryGetValue(rootId, out var root) || !root.Visible) return;
        if (_panels.TryGetValue(rootId, out var panel) && panel.Visible) panel.BringGroupToFront();
        else SetWindowPos(root.Handle, new nint(-1), 0, 0, 0, 0, 0x0013);
    }

    private void ConfigureManagedForm(StickerForm form)
    {
        form.ManagedTreeInteractions = true;
        ConfigureSelection(form);
        form.TreeDragRequested += BeginManagedDrag;
        form.HideRequested += value => _ = HideTreeAsync(value.Bookmark.Id);
        form.RequestGroupActivation += value => BringTreeToFront(RootId(value.Bookmark.Id));
        form.LocationChanged += (_, _) =>
        {
            if (!_applying && !_rebuildingPanels && !form.IsTreeEmbedded && _drag is null) RebuildPanels();
        };
    }

    private async void RequestDetach(Bookmark item)
    {
        if (!await CommitPendingEditsAsync(item.Id) || !_forms.TryGetValue(item.Id, out var form)) return;
        var root = _forms[RootId(item.Id)];
        Rectangle occupied = root.ScreenBounds;
        if (_panels.TryGetValue(root.Bookmark.Id, out var panel) && panel.Visible) occupied = Rectangle.Union(occupied, panel.Bounds);
        var screen = Screen.FromControl(root);
        int gap = Math.Max(8, root.DeviceDpi / 12);
        Size size = root.Size;
        Point[] candidates = [new(occupied.Right + gap, occupied.Top), new(occupied.Left - gap - size.Width, occupied.Top),
            new(occupied.Left, occupied.Bottom + gap), new(occupied.Left, occupied.Top - gap - size.Height)];
        var peers = RootRectangles().ToArray();
        int contentHeight = VisibleSubtreeContentHeight(item.Id, root.DeviceDpi);
        Rectangle bounds = candidates.Select(point => new Rectangle(point, size))
            .FirstOrDefault(candidate =>
            {
                var plan = StickerTreeLayout.PlanViewport(candidate, screen.WorkingArea, contentHeight, root.DeviceDpi);
                var footprint = plan is null ? candidate : Rectangle.Union(candidate, plan.Bounds);
                return screen.WorkingArea.Contains(candidate) && (contentHeight == 0 || plan is not null) &&
                    peers.All(peer => !peer.IntersectsWith(footprint));
            });
        if (bounds.IsEmpty) bounds = ClampBounds(new Rectangle(root.Location + new Size(gap * 3, gap * 3), size), screen.WorkingArea, root.MinimumSize);
        var layout = LayoutAt(item.Id, bounds, screen, root.DeviceDpi);
        DropRequested?.Invoke(new(item.Id, BookmarkDropKind.Detach, RootLayout: layout));
        if (DropRequested is null) ParentChangeRequested?.Invoke(item, null);
    }

    private IEnumerable<Rectangle> RootRectangles(Guid? excluded = null)
    {
        foreach (var root in _forms.Values.Where(form => !form.IsTreeEmbedded && form.Visible && RootId(form.Bookmark.Id) == form.Bookmark.Id))
        {
            if (root.Bookmark.Id == excluded) continue;
            yield return _panels.TryGetValue(root.Bookmark.Id, out var panel) && panel.Visible
                ? Rectangle.Union(root.ScreenBounds, panel.Bounds) : root.ScreenBounds;
        }
    }

    private static StickerLayout LayoutAt(Guid id, Rectangle bounds, Screen screen, int dpi)
    {
        float scale = 96F / Math.Max(96, dpi);
        return new(id, screen.DeviceName, (int)Math.Round((bounds.Left - screen.WorkingArea.Left) * scale),
            (int)Math.Round((bounds.Top - screen.WorkingArea.Top) * scale),
            (int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale), false, true, null);
    }

    private static Size RootMinimumSizeAtDpi(int dpi)
    {
        dpi = Math.Max(96, dpi);
        if (RootMinimumSizes.TryGetValue(dpi, out var size)) return size;
        // Native frame rounding is not linear across monitors. This template uses
        // the same caption-free tool-window styles as a floating StickerForm.
        using var template = new BoundsTemplate(includeCaption: false);
        size = template.OuterSize(new Size(
            (int)Math.Round(StickerForm.MinimumClientSize.Width * dpi / 96d),
            (int)Math.Round(StickerForm.DefaultClientSize.Height * dpi / 96d)), dpi);
        RootMinimumSizes[dpi] = size;
        return size;
    }

    private static Rectangle ClampRootBounds(Rectangle bounds, Rectangle area, int dpi)
    {
        Size minimum = RootMinimumSizeAtDpi(dpi);
        bounds.Height = minimum.Height;
        return ClampBounds(bounds, area, minimum);
    }

    private static Rectangle RestoreRootBounds(StickerLayout layout, Rectangle area, int dpi)
    {
        double scale = Math.Max(96, dpi) / 96d;
        return ClampRootBounds(new Rectangle(area.Left + (int)Math.Round(layout.Left * scale),
            area.Top + (int)Math.Round(layout.Top * scale), (int)Math.Round(layout.Width * scale), 0), area, dpi);
    }

    private async void BeginManagedDrag(StickerForm source, Point pointerDown)
    {
        if (_drag is not null || _layoutCommitting || _disposed || !_enabled || source.IsEditingNote) return;
        if (!SelectedBookmarkIds.Contains(source.Bookmark.Id)) SelectBookmark(source, Keys.None, preserveSelection: true);
        var selectedIds = MoveSelection(source);
        source = SelectionAnchor(source, selectedIds);
        int startVersion = ++_dragStartVersion;
        foreach (Guid id in selectedIds)
            if (!await CommitPendingEditsAsync(id)) return;
        if ((Control.MouseButtons & MouseButtons.Left) == 0) return;
        try { await SavePendingAsync(); } catch { return; }
        // A storage wait can outlive the physical gesture or a hide/mode change.
        if (startVersion != _dragStartVersion || _disposed || !_enabled || _drag is not null ||
            _layoutCommitting || source.IsDisposed || !source.Visible || (Control.MouseButtons & MouseButtons.Left) == 0 ||
            !selectedIds.SequenceEqual(MoveSelection(source))) return;
        Rectangle bounds = source.ScreenBounds;
        var ghost = new StickerDragPreview();
        _drag = new DragSession(source, bounds, new Size(pointerDown.X - bounds.X, pointerDown.Y - bounds.Y), ghost, selectedIds.ToArray());
        source.Capture = true;
        Application.AddMessageFilter(this);
        _dragTimer.Tick -= DragTick;
        _dragTimer.Tick += DragTick;
        _dragTimer.Start();
        UpdateManagedDrag();
        ghost.Show();
    }

    private void DragTick(object? sender, EventArgs e) => UpdateManagedDrag();

    public bool PreFilterMessage(ref Message message)
    {
        if (_drag is null) return false;
        if (message.Msg is 0x0100 or 0x0104 && (Keys)(int)message.WParam == Keys.Escape)
        { CancelManagedDrag(); return true; }
        if (message.Msg == 0x0202) { _ = CompleteManagedDragAsync(); return true; }
        if (message.Msg is 0x0200 or 0x0101 or 0x0105) UpdateManagedDrag();
        return false;
    }

    private void UpdateManagedDrag()
    {
        if (_drag is not { } drag) return;
        if (!IsDragSelectionCurrent(drag)) { CancelManagedDrag(); return; }
        var decision = EvaluateDrop(drag.Source, Cursor.Position, drag.GrabOffset, Control.ModifierKeys);
        drag.Preview = decision;
        drag.Ghost.SetPreview(decision.PanelBounds is Rectangle panel ? Rectangle.Union(decision.Bounds, panel) : decision.Bounds,
            decision.Label, decision.Valid);
        UpdateSelectionGhosts(drag, decision);
    }

    internal DropPreview EvaluateDrop(StickerForm source, Point cursor, Size grabOffset, Keys modifiers)
    {
        var selectedIds = MoveSelection(source);
        var anchor = SelectionAnchor(source, selectedIds);
        if (anchor != source)
            grabOffset += new Size(source.ScreenBounds.Left - anchor.ScreenBounds.Left, source.ScreenBounds.Top - anchor.ScreenBounds.Top);
        return selectedIds.Count > 1 ? EvaluateSelectionDrop(anchor, selectedIds, cursor, grabOffset, modifiers)
            : EvaluateSingleDrop(anchor, cursor, grabOffset, modifiers);
    }

    private DropPreview EvaluateSingleDrop(StickerForm source, Point cursor, Size grabOffset, Keys modifiers, IReadOnlySet<Guid>? movingIds = null)
    {
        if (_disposed || source.IsDisposed || !source.Visible || !_forms.ContainsKey(source.Bookmark.Id) || !_forms.ContainsKey(RootId(source.Bookmark.Id)))
            return new(false, null, Rectangle.Empty, "이동할 항목이 없습니다", false);
        bool root = RootId(source.Bookmark.Id) == source.Bookmark.Id;
        var screen = Screen.FromPoint(cursor);
        var rootForm = _forms[RootId(source.Bookmark.Id)];
        int dpi = MonitorDpi(screen, rootForm.DeviceDpi);
        var baseLayout = CaptureLayout(rootForm);
        int width = (int)Math.Round(baseLayout.Width * dpi / 96d);
        var offset = new Size((int)Math.Round(grabOffset.Width * dpi / (double)Math.Max(96, rootForm.DeviceDpi)),
            (int)Math.Round(grabOffset.Height * dpi / (double)Math.Max(96, rootForm.DeviceDpi)));
        Rectangle proposed = ClampRootBounds(new Rectangle(cursor.X - offset.Width, cursor.Y - offset.Height, width, 0),
            screen.WorkingArea, dpi);
        if ((modifiers & Keys.Alt) != 0)
            return root ? RootMovePreview(source, proposed, screen, dpi, "트리 이동 · 자석/묶기 끔", false)
                : new(false, null, proposed, "이동 취소 · Alt", false);
        var excluded = (movingIds ?? new HashSet<Guid> { source.Bookmark.Id })
            .SelectMany(id => Subtree(id)).Select(row => row.Bookmark.Id).ToHashSet();
        var hit = HitSurface(cursor, root ? source.Bookmark.Id : null, movingIds);
        StickerForm? target = null;
        if (hit is StickerForm card && !excluded.Contains(card.Bookmark.Id)) target = card;
        if (hit is StickerTreePanel treePanel)
            target = treePanel.Rows.Select(row => row.Form).FirstOrDefault(form => !excluded.Contains(form.Bookmark.Id) && form.Visible &&
                ContainsCardPoint(form, cursor));
        if (target is not null && !target.IsEditingNote)
        {
            var targetBounds = target.ScreenBounds;
            int band = Math.Max(6, Math.Min(12, targetBounds.Height / 4));
            bool before = cursor.Y < targetBounds.Top + band;
            bool after = cursor.Y >= targetBounds.Bottom - band;
            // Root cards have no positional sibling list on the desktop.
            if ((before || after) && target.Bookmark.ParentId is Guid parent && BookmarkTree.CanReparent(_forms.Values.Select(form => form.Bookmark), source.Bookmark.Id, parent))
            {
                var siblings = Tree().Where(row => row.Bookmark.ParentId == parent && !excluded.Contains(row.Bookmark.Id)).Select(row => row.Bookmark).ToArray();
                int index = Array.FindIndex(siblings, item => item.Id == target.Bookmark.Id);
                Guid? beforeId = before ? target.Bookmark.Id : index + 1 < siblings.Length ? siblings[index + 1].Id : null;
                return new(true, new(source.Bookmark.Id, BookmarkDropKind.InsertBefore, parent, beforeId),
                    new Rectangle(targetBounds.Left, before ? targetBounds.Top : targetBounds.Bottom, targetBounds.Width, 4), "이 위치로 이동", false);
            }
            if (target.Bookmark.IsFolder && BookmarkTree.CanReparent(_forms.Values.Select(form => form.Bookmark), source.Bookmark.Id, target.Bookmark.Id))
                return new(true, new(source.Bookmark.Id, BookmarkDropKind.MoveInto, target.Bookmark.Id), targetBounds, $"{target.Bookmark.DisplayName} 안으로 이동", false);
            // A narrow, visibly labelled central zone distinguishes grouping from placement.
            var groupZone = new Rectangle(targetBounds.Left + targetBounds.Width / 4, targetBounds.Top + band,
                targetBounds.Width / 2, Math.Max(1, targetBounds.Height - band * 2));
            if (!target.Bookmark.IsFolder && groupZone.Contains(cursor) && BookmarkTree.CanDropOnto(_forms.Values.Select(form => form.Bookmark), source.Bookmark.Id, target.Bookmark.Id))
            {
                StickerLayout? origin = target.Bookmark.ParentId is null ? CaptureLayout(target) : null;
                return new(true, new(source.Bookmark.Id, BookmarkDropKind.Group, target.Bookmark.Id, RootLayout: origin), targetBounds, "두 항목을 새 폴더로 묶기", false);
            }
        }
        if (!root && hit is not null) return new(false, null, proposed, "삽입선 또는 이동 영역에 놓으세요", false);
        if (!root)
        {
            var layout = LayoutAt(source.Bookmark.Id, proposed, screen, dpi);
            proposed = RestoreRootBounds(layout, screen.WorkingArea, dpi);
            int height = VisibleSubtreeContentHeight(source.Bookmark.Id, dpi);
            var plan = StickerTreeLayout.PlanViewport(proposed, screen.WorkingArea, height, dpi);
            return new(true, new(source.Bookmark.Id, BookmarkDropKind.Detach, RootLayout: layout),
                proposed, "최상위로 꺼내기", false, plan?.Bounds, dpi);
        }
        return RootMovePreview(source, proposed, screen, dpi, "트리 이동", SnapEnabled && movingIds is null);
    }

    private int VisibleSubtreeContentHeight(Guid id, int dpi)
    {
        var visible = FolderVisibleIds();
        return StickerTreeLayout.MeasureContentHeight(Subtree(id).Skip(1)
            .Where(row => visible.Contains(row.Bookmark.Id)).Select(row => _forms[row.Bookmark.Id].TreeRowHeightAtDpi(dpi)), dpi);
    }

    private DropPreview RootMovePreview(StickerForm source, Rectangle proposed, Screen screen, int dpi, string label, bool snap)
    {
        proposed = ClampRootBounds(proposed, screen.WorkingArea, dpi);
        int height = _panels.TryGetValue(source.Bookmark.Id, out var panel)
            ? StickerTreeLayout.MeasureContentHeight(panel.Rows.Select(row => row.Form.TreeRowHeightAtDpi(dpi)), dpi) : 0;
        var plan = StickerTreeLayout.PlanViewport(proposed, screen.WorkingArea, height, dpi);
        if (snap)
        {
            Rectangle footprint = plan is null ? proposed : Rectangle.Union(proposed, plan.Bounds);
            var aligned = StickerSnap.Snap(footprint, RootRectangles(source.Bookmark.Id).ToArray(), screen.WorkingArea, dpi);
            proposed.Offset(aligned.X - footprint.X, aligned.Y - footprint.Y);
            plan = StickerTreeLayout.PlanViewport(proposed, screen.WorkingArea, height, dpi);
        }
        // Quantize to the same logical coordinates storage will restore.
        var layout = LayoutAt(source.Bookmark.Id, proposed, screen, dpi);
        proposed = RestoreRootBounds(layout, screen.WorkingArea, dpi);
        plan = StickerTreeLayout.PlanViewport(proposed, screen.WorkingArea, height, dpi);
        return new(true, null, proposed, label, true, plan?.Bounds, dpi);
    }

    private static int MonitorDpi(Screen screen, int fallback)
    {
        try
        {
            nint monitor = MonitorFromPoint(new NativePoint { X = screen.Bounds.Left, Y = screen.Bounds.Top }, 2);
            if (GetDpiForMonitor(monitor, 0, out uint x, out _) == 0 && x > 0) return (int)x;
        }
        catch (DllNotFoundException) { }
        return fallback;
    }

    private Form? HitSurface(Point cursor, Guid? excludedRoot, IReadOnlySet<Guid>? excludedRoots = null)
    {
        var surfaces = new Dictionary<nint, Form>();
        foreach (var form in _forms.Values.Where(form => !form.IsTreeEmbedded && form.Visible && form.Bookmark.Id != excludedRoot && excludedRoots?.Contains(form.Bookmark.Id) != true))
            if (ContainsCardPoint(form, cursor)) surfaces[form.Handle] = form;
        foreach (var (id, panel) in _panels)
            if (id != excludedRoot && excludedRoots?.Contains(id) != true && panel.ContainsVisiblePoint(cursor)) surfaces[panel.Handle] = panel;
        // The panel's rectangle still measures its layout, but transparent gaps
        // and clipped corners must not cover a lower card during drop targeting.
        for (nint window = GetTopWindow(nint.Zero); window != nint.Zero; window = GetWindow(window, 2))
            if (surfaces.TryGetValue(window, out var surface)) return surface;
        return null;
    }

    private static bool ContainsCardPoint(StickerForm form, Point cursor) =>
        form.ScreenBounds.Contains(cursor) &&
        (!form.IsTreeEmbedded || form.Parent is { } viewport && viewport.ClientRectangle.Contains(viewport.PointToClient(cursor))) &&
        (form.Region is null || form.Region.IsVisible(form.PointToClient(cursor)));

    private async Task CompleteManagedDragAsync()
    {
        if (_drag is not { } drag) return;
        if (!IsDragSelectionCurrent(drag)) { CancelManagedDrag(); return; }
        DropPreview? preview = drag.Preview;
        Point cursor = Cursor.Position;
        Keys modifiers = Control.ModifierKeys;
        CancelManagedDrag();
        if (preview is not null) await CommitDropPreviewAsync(drag.Source, preview, cursor, drag.GrabOffset, modifiers);
    }

    internal async Task<bool> CommitDropPreviewAsync(StickerForm source, DropPreview preview, Point cursor, Size grabOffset, Keys modifiers)
    {
        var final = EvaluateDrop(source, cursor, grabOffset, modifiers);
        if (!preview.Valid || !SameDropPreview(preview, final)) return false;
        if (preview.Request is { } request)
        {
            DropRequested?.Invoke(request);
            return DropRequested is not null;
        }
        if (preview.IsRootMove)
        {
            if (preview.Moves is { Count: > 0 } moves)
            {
                bool committed = await CommitLayoutsAsync(moves.Select(move => move.Layout).ToArray());
                foreach (var move in moves) BringTreeToFront(move.BookmarkId);
                return committed;
            }
            source = SelectionAnchor(source, MoveSelection(source));
            Screen screen = Screen.FromRectangle(preview.Bounds);
            var layout = LayoutAt(source.Bookmark.Id, preview.Bounds, screen, preview.Dpi);
            bool saved = await CommitLayoutsAsync([layout]);
            BringTreeToFront(source.Bookmark.Id);
            return saved;
        }
        return false;
    }

    private void CancelManagedDrag()
    {
        ++_dragStartVersion;
        if (_drag is not { } drag) return;
        _drag = null;
        _dragTimer.Stop();
        Application.RemoveMessageFilter(this);
        if (!drag.Source.IsDisposed) drag.Source.Capture = false;
        drag.Ghost.Dispose();
        foreach (var ghost in drag.SelectionGhosts) ghost.Dispose();
    }

    private async Task PersistPlacedRootAsync(StickerForm form)
    {
        var desired = CaptureLayout(form);
        if (_layouts.TryGetValue(form.Bookmark.Id, out var previous) && previous == desired) return;
        if (!await CommitLayoutsAsync([desired]) && !_disposed && _committedLayouts.TryGetValue(form.Bookmark.Id, out var committed))
            ApplySavedLayouts([committed]);
    }

    private Task<bool> CommitLayoutsAsync(IReadOnlyList<StickerLayout> layouts)
    {
        if (_disposed) return Task.FromResult(false);
        var preceding = _layoutOperation;
        int generation = ++_layoutGeneration;
        _layoutCommitting = true;
        var operation = CommitLayoutsCoreAsync(layouts, preceding, generation);
        _layoutOperation = operation.IsCompleted ? null : operation;
        return operation;
    }

    private async Task<bool> CommitLayoutsCoreAsync(IReadOnlyList<StickerLayout> layouts, Task<bool>? preceding, int generation)
    {
        try
        {
            if (preceding is not null) await preceding;
            await FlushPendingAsync();
            await Task.Run(() => _save(layouts));
            if (_disposed) return false;
            ApplySavedLayouts(layouts);
            return true;
        }
        catch
        {
            if (!_disposed) _notify("배치를 저장하지 못했습니다. 이전 위치를 유지합니다.");
            return false;
        }
        finally { if (generation == _layoutGeneration) { _layoutCommitting = false; _layoutOperation = null; } }
    }

    internal sealed record DropPreview(bool Valid, BookmarkDropRequest? Request, Rectangle Bounds, string Label, bool IsRootMove,
        Rectangle? PanelBounds = null, int Dpi = 96, IReadOnlyList<SelectionMovePreview>? Moves = null);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    private bool IsDragSelectionCurrent(DragSession drag) => !drag.Source.IsDisposed && drag.Source.Visible &&
        drag.SelectionIds.All(id => _forms.TryGetValue(id, out var form) && !form.IsDisposed && form.Visible) &&
        drag.SelectionIds.SequenceEqual(MoveSelection(drag.Source));

    private sealed class DragSession(StickerForm source, Rectangle origin, Size grabOffset, StickerDragPreview ghost, Guid[] selectionIds)
    {
        internal StickerForm Source { get; } = source;
        internal Rectangle Origin { get; } = origin;
        internal Size GrabOffset { get; } = grabOffset;
        internal StickerDragPreview Ghost { get; } = ghost;
        internal Guid[] SelectionIds { get; } = selectionIds;
        internal List<StickerDragPreview> SelectionGhosts { get; } = [];
        internal DropPreview? Preview { get; set; }
    }
}
