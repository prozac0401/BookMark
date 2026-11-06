using System.Drawing;
using System.Runtime.InteropServices;

namespace WorkBookmark.App.UI;

/// <summary>A bounded viewport whose native region contains only visible cards and scrollbars.</summary>
internal sealed class StickerTreePanel : Form
{
    private static readonly Color CardBackdrop = Color.FromArgb(221, 228, 231);
    private readonly Panel _viewport = new() { AutoScroll = true };
    private readonly List<(StickerForm Form, int Depth)> _rows = [];
    private Rectangle _workingArea;
    private StickerTreeOpeningSide? _openingSide;
    private bool _layingOut, _updatingRegion;
    private Region? _visibleCardRegion;

    internal StickerForm? Root { get; private set; }
    internal IReadOnlyList<(StickerForm Form, int Depth)> Rows => _rows;
    internal Rectangle VisibleBounds => Visible ? Bounds : Rectangle.Empty;
    internal int ContentHeight { get; private set; }
    internal StickerTreeOpeningSide? OpeningSide => _openingSide;
    internal event Action<StickerForm>? GroupActivationRequested;
    internal event Action<StickerForm>? SpaceUnavailable;

    protected override bool ShowWithoutActivation => true;

    protected override void SetVisibleCore(bool value)
    {
        if (!value || Visible) { base.SetVisibleCore(value); return; }
        var recent = Form.ActiveForm as RecentForm;
        nint recentWindow = recent is { IsDisposed: false, IsHandleCreated: true, Visible: true } &&
            GetForegroundWindow() == recent.Handle ? recent.Handle : nint.Zero;
        using (new StickerForm.NonActivatingShowScope(this)) base.SetVisibleCore(true);
        if (!IsDisposed && IsHandleCreated && Visible && recentWindow != nint.Zero &&
            recent is { IsDisposed: false, IsHandleCreated: true, Visible: true } && GetForegroundWindow() == recentWindow)
            SetWindowPos(Handle, recentWindow, 0, 0, 0, 0, 0x0013);
    }

    internal StickerTreePanel()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        MaximizeBox = MinimizeBox = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = _viewport.BackColor = CardBackdrop;
        AccessibleName = "폴더 책갈피 목록";
        Controls.Add(_viewport);
        _viewport.MouseDown += (_, _) => ActivateGroup();
        _viewport.Scroll += (_, _) => { UpdateVisibleRegion(); Invalidate(); };
        _viewport.Layout += (_, _) => { if (!_layingOut) UpdateVisibleRegion(); };
    }

    internal bool SetRows(StickerForm root, IReadOnlyList<(StickerForm Form, int Depth)> rows,
        Rectangle workingArea, bool resetDirection = false)
    {
        if (IsDisposed) return false;
        using var activation = new StickerForm.NonActivatingShowScope(this);
        if (!ReferenceEquals(Root, root))
        {
            DetachAll();
            Root = root;
            Owner = root;
            resetDirection = true;
        }
        if (_workingArea != workingArea) resetDirection = true;
        _workingArea = workingArea;
        if (resetDirection) _openingSide = null;
        if (!IsHandleCreated)
        {
            // Resolve native/DPI sizing before applying the preview geometry;
            // first-show autoscaling must not change the planned panel edge.
            Location = root.Location;
            _ = Handle;
        }
        var desired = rows.Select(row => row.Form).ToHashSet();
        foreach (var removed in _rows.Where(row => !desired.Contains(row.Form)).ToArray()) HideRow(removed.Form);
        foreach (var (form, _) in rows)
        {
            if (!_rows.Any(row => ReferenceEquals(row.Form, form)))
            {
                form.FootprintChanged += RowFootprintChanged;
                form.RequestGroupActivation += RowActivated;
                form.LocationChanged += RowShapeChanged;
                form.SizeChanged += RowShapeChanged;
                form.RegionChanged += RowShapeChanged;
                form.VisibleChanged += RowShapeChanged;
            }
            form.SetTreeEmbedded(_viewport);
        }
        _rows.Clear();
        _rows.AddRange(rows);
        if (_rows.Count == 0) _openingSide = null;
        return Reflow();
    }

    internal void ResetOpeningDirection() => _openingSide = null;

    internal bool Reflow()
    {
        if (_layingOut || IsDisposed || Root is not { IsDisposed: false } root) return false;
        _layingOut = true;
        try
        {
            ContentHeight = StickerTreeLayout.MeasureContentHeight(_rows.Select(row => row.Form.TreeRowHeightAtDpi(root.DeviceDpi)), root.DeviceDpi);
            var plan = StickerTreeLayout.PlanViewport(root.Bounds, _workingArea, ContentHeight, root.DeviceDpi, _openingSide);
            if (plan is null || _rows.Count == 0)
            {
                Hide();
                UpdateVisibleRegion();
                return false;
            }
            _openingSide = plan.Side;
            int scroll = Math.Max(0, -_viewport.AutoScrollPosition.Y);
            // Clear the previous native scrollbar state before resizing. Leaving
            // both bars present can make each one require the other even after
            // the new content fits exactly. This layout must run before suspension
            // so AutoScroll=false actually removes the old bars and scroll offset.
            _viewport.AutoScrollPosition = Point.Empty;
            _viewport.AutoScrollMinSize = Size.Empty;
            _viewport.AutoScroll = false;
            Bounds = plan.Bounds;
            // Crossing monitors can synchronously autoscale this first assignment
            // inside WM_DPICHANGED. Once that native transition returns, restore
            // the root-derived physical bounds instead of retaining a second scale.
            if (Bounds != plan.Bounds) Bounds = plan.Bounds;
            int inset = root.PointToScreen(Point.Empty).X - root.Left;
            _viewport.Bounds = new Rectangle(inset, 0, root.ClientSize.Width, plan.Bounds.Height);
            // The native host region removes the backdrop outside the cards.
            BackColor = _viewport.BackColor = CardBackdrop;
            int border = Scale(1), gap = Scale(8);
            _viewport.SuspendLayout();
            try
            {
                _viewport.AutoScrollMinSize = new Size(0, ContentHeight + border * 2);
                // Resizing can temporarily leave both native scrollbars visible for
                // the previous content extent. Derive the row width from the final
                // plan, not that transient ClientSize, so shrinking restores it.
                int scrollbar = ContentHeight + border * 2 > plan.Bounds.Height
                    ? GetSystemMetricsForDpi(2, (uint)Math.Max(96, root.DeviceDpi)) : 0; // SM_CXVSCROLL
                int width = Math.Max(1, root.ClientSize.Width - scrollbar);
                int y = border;
                foreach (var (form, depth) in _rows)
                {
                    form.Bounds = new Rectangle(0, y, width, form.TreeRowHeightAtDpi(root.DeviceDpi));
                    form.SetTreeIndent(depth);
                    form.Show();
                    y += form.Height + gap;
                }
            }
            finally { _viewport.ResumeLayout(true); }
            _viewport.AutoScrollPosition = new Point(0, Math.Min(scroll, Math.Max(0, ContentHeight + border * 2 - _viewport.ClientSize.Height)));
            UpdateVisibleRegion(includeRowsBeforeShow: true);
            if (root.Visible && !Visible) Show();
            foreach (var (form, _) in _rows)
                if (form.IsEditingNote && form.ContainsFocus) EnsureEditorVisible(form);
            return true;
        }
        finally { _layingOut = false; }
    }

    internal void EnsureEditorVisible(StickerForm form)
    {
        if (IsDisposed || !Visible || !_rows.Any(row => ReferenceEquals(row.Form, form))) return;
        Rectangle input = form.RectangleToScreen(form.EditorFocusBounds);
        Rectangle visible = _viewport.RectangleToScreen(_viewport.ClientRectangle);
        int scroll = Math.Max(0, -_viewport.AutoScrollPosition.Y);
        if (input.Top < visible.Top) scroll += input.Top - visible.Top;
        else if (input.Bottom > visible.Bottom) scroll += input.Bottom - visible.Bottom;
        _viewport.AutoScrollPosition = new Point(0, Math.Clamp(scroll, 0, Math.Max(0, ContentHeight + Scale(1) * 2 - _viewport.ClientSize.Height)));
        UpdateVisibleRegion();
    }

    internal void EnsureItemVisible(StickerForm form) => EnsureEditorVisible(form);

    internal void DetachRow(StickerForm form)
    {
        _rows.RemoveAll(row => ReferenceEquals(row.Form, form));
        form.FootprintChanged -= RowFootprintChanged;
        form.RequestGroupActivation -= RowActivated;
        form.LocationChanged -= RowShapeChanged;
        form.SizeChanged -= RowShapeChanged;
        form.RegionChanged -= RowShapeChanged;
        form.VisibleChanged -= RowShapeChanged;
        if (!form.IsDisposed && ReferenceEquals(form.Parent, _viewport)) form.SetTreeEmbedded(null);
        if (!_layingOut) UpdateVisibleRegion();
    }

    private void HideRow(StickerForm form)
    {
        _rows.RemoveAll(row => ReferenceEquals(row.Form, form));
        form.FootprintChanged -= RowFootprintChanged;
        form.RequestGroupActivation -= RowActivated;
        form.LocationChanged -= RowShapeChanged;
        form.SizeChanged -= RowShapeChanged;
        form.RegionChanged -= RowShapeChanged;
        form.VisibleChanged -= RowShapeChanged;
        // Folding changes visibility, not window identity. Recreating an unseen
        // top-level window here can activate it and steal focus from the list.
        if (!form.IsDisposed && ReferenceEquals(form.Parent, _viewport)) form.HideForFolder();
    }

    internal void DetachAll()
    {
        foreach (var form in _rows.Select(row => row.Form).Concat(_viewport.Controls.OfType<StickerForm>()).Distinct().ToArray()) DetachRow(form);
        ContentHeight = 0;
        _openingSide = null;
        if (!IsDisposed) Hide();
    }

    internal void BringGroupToFront()
    {
        if (Root is { IsDisposed: false, IsHandleCreated: true, Visible: true } root)
            SetWindowPos(root.Handle, new nint(-1), 0, 0, 0, 0, 0x0013);
        if (IsHandleCreated && Visible) SetWindowPos(Handle, new nint(-1), 0, 0, 0, 0, 0x0013);
    }

    private void ActivateGroup()
    {
        BringGroupToFront();
        if (Root is { } root) GroupActivationRequested?.Invoke(root);
    }

    private void RowActivated(StickerForm form)
    {
        ActivateGroup();
        EnsureEditorVisible(form);
    }

    private void RowFootprintChanged(StickerForm form)
    {
        if (_layingOut) return;
        if (!Reflow() && Root is { } root && _rows.Count > 0) SpaceUnavailable?.Invoke(root);
        EnsureEditorVisible(form);
    }

    private void RowShapeChanged(object? sender, EventArgs e)
    {
        if (!_layingOut) UpdateVisibleRegion();
    }

    internal bool ContainsVisiblePoint(Point screenPoint) => !IsDisposed && Visible && IsHandleCreated &&
        Bounds.Contains(screenPoint) && Region?.IsVisible(PointToClient(screenPoint)) == true;

    private void UpdateVisibleRegion(bool includeRowsBeforeShow = false)
    {
        if (_updatingRegion || IsDisposed || Disposing || !IsHandleCreated || _viewport.IsDisposed) return;
        _updatingRegion = true;
        try
        {
            var shape = new Region();
            shape.MakeEmpty();
            Rectangle clip = RectangleToClient(_viewport.RectangleToScreen(_viewport.ClientRectangle));
            clip.Intersect(ClientRectangle);
            foreach (var (form, _) in _rows)
            {
                if (form.IsDisposed || !ReferenceEquals(form.Parent, _viewport) ||
                    !form.Visible && !(includeRowsBeforeShow && !Visible)) continue;
                using var card = form.Region?.Clone() ?? new Region(form.ClientRectangle);
                Point origin = PointToClient(form.PointToScreen(Point.Empty));
                card.Translate(origin.X, origin.Y);
                card.Intersect(clip);
                shape.Union(card);
            }
            // Native scrollbars are outside the viewport's client rectangle. Include
            // their actual visible strips, never the transparent gaps between cards.
            Rectangle viewportBounds = _viewport.Bounds;
            if (_rows.Count > 0 && _viewport.VerticalScroll.Visible && clip.Right < viewportBounds.Right)
                shape.Union(new Rectangle(clip.Right, viewportBounds.Top,
                    viewportBounds.Right - clip.Right, viewportBounds.Height));
            if (_rows.Count > 0 && _viewport.HorizontalScroll.Visible && clip.Bottom < viewportBounds.Bottom)
                shape.Union(new Rectangle(viewportBounds.Left, clip.Bottom,
                    viewportBounds.Width, viewportBounds.Bottom - clip.Bottom));
            shape.Intersect(ClientRectangle);
            var previous = _visibleCardRegion;
            _visibleCardRegion = shape;
            Region = shape;
            previous?.Dispose();
        }
        finally { _updatingRegion = false; }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        ActivateGroup();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        foreach (var (form, _) in _rows) form.NotifyHostDeactivated();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Root?.RequestHide();
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DetachAll();
            Region = null;
            _visibleCardRegion?.Dispose();
            _visibleCardRegion = null;
        }
        base.Dispose(disposing);
    }

    private int Scale(int value) => Math.Max(1, (int)Math.Round(value * Math.Max(96, Root?.DeviceDpi ?? DeviceDpi) / 96d));

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
