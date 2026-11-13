using System.Drawing;
using WorkBookmark.App.UI;
using WorkBookmark.Core;

namespace WorkBookmark.App;

internal sealed partial class StickerManager
{
    private IReadOnlyList<Guid> MoveSelection(StickerForm source) =>
        SelectedBookmarkIds.Contains(source.Bookmark.Id)
            ? BookmarkTree.SelectionRoots(_forms.Values.Select(form => form.Bookmark), SelectedBookmarkIds)
            : [source.Bookmark.Id];

    private StickerForm SelectionAnchor(StickerForm source, IReadOnlyList<Guid> ids)
    {
        if (ids.Contains(source.Bookmark.Id)) return source;
        return ids.Select(id => _forms.GetValueOrDefault(id)).FirstOrDefault(form => form is not null &&
            Subtree(form.Bookmark.Id).Any(row => row.Bookmark.Id == source.Bookmark.Id)) ?? source;
    }

    private DropPreview EvaluateSelectionDrop(StickerForm source, IReadOnlyList<Guid> ids, Point cursor, Size grabOffset, Keys modifiers)
    {
        if (ids.Any(id => !_forms.TryGetValue(id, out var form) || form.IsDisposed || !form.Visible))
            return new(false, null, Rectangle.Empty, "이동할 항목이 없습니다", false);
        bool allRoots = ids.All(id => RootId(id) == id);
        if ((modifiers & Keys.Alt) != 0 && !allRoots)
            return new(false, null, source.ScreenBounds, "이동 취소 · Alt", false);

        var decision = EvaluateSingleDrop(source, cursor, grabOffset, modifiers, ids.ToHashSet());
        if (!decision.Valid) return decision;
        if (decision.Request is { Kind: not BookmarkDropKind.Detach } request)
        {
            var items = _forms.Values.Select(form => form.Bookmark).ToArray();
            bool allowed = request.Kind == BookmarkDropKind.Group
                ? request.TargetId is Guid target && ids.All(id => BookmarkTree.CanDropOnto(items, id, target))
                : ids.All(id => BookmarkTree.CanReparent(items, id, request.TargetId));
            if (!allowed) return decision with { Valid = false, Request = null, Label = "선택한 항목이나 그 하위로 옮길 수 없습니다" };
            return decision with
            {
                Request = request with { BookmarkIds = ids },
                Label = request.Kind == BookmarkDropKind.Group ? $"선택한 {ids.Count}개와 대상을 새 폴더로 묶기"
                    : $"{ids.Count}개 · {decision.Label}"
            };
        }

        // Every selected root follows the same pointer displacement. Only an off-screen
        // card is clamped by its destination monitor; derived child rows are never saved.
        var delta = new Size(decision.Bounds.Left - source.ScreenBounds.Left, decision.Bounds.Top - source.ScreenBounds.Top);
        var proposed = ids.ToDictionary(id => id, id =>
        {
            var form = _forms[id];
            var origin = form.ScreenBounds;
            var location = origin.Location + delta;
            var screen = Screen.FromPoint(new Point(location.X + origin.Width / 2, location.Y + origin.Height / 2));
            int dpi = MonitorDpi(screen, form.DeviceDpi);
            int width = (int)Math.Round(CaptureLayout(_forms[RootId(id)]).Width * dpi / 96d);
            return new Rectangle(location, new Size(width, RootMinimumSizeAtDpi(dpi).Height));
        });
        var envelope = proposed.Values.Aggregate(Rectangle.Union);
        var destination = Screen.FromPoint(cursor);
        // At screen edges retain the spacing of a group that fits on this monitor.
        if (proposed.Values.All(bounds => Screen.FromRectangle(bounds).DeviceName == destination.DeviceName) &&
            envelope.Width <= destination.WorkingArea.Width && envelope.Height <= destination.WorkingArea.Height)
        {
            int dx = Math.Clamp(envelope.Left, destination.WorkingArea.Left, destination.WorkingArea.Right - envelope.Width) - envelope.Left;
            int dy = Math.Clamp(envelope.Top, destination.WorkingArea.Top, destination.WorkingArea.Bottom - envelope.Height) - envelope.Top;
            if (SnapEnabled && (modifiers & Keys.Alt) == 0)
            {
                var bounded = envelope; bounded.Offset(dx, dy);
                var peers = _forms.Values.Where(form => !form.IsTreeEmbedded && form.Visible && !ids.Contains(form.Bookmark.Id))
                    .Select(form => _panels.TryGetValue(form.Bookmark.Id, out var panel) && panel.Visible
                        ? Rectangle.Union(form.ScreenBounds, panel.Bounds) : form.ScreenBounds).ToArray();
                var snapped = StickerSnap.Snap(bounded, peers, destination.WorkingArea, decision.Dpi);
                dx += snapped.Left - bounded.Left; dy += snapped.Top - bounded.Top;
            }
            foreach (Guid id in ids) { var bounds = proposed[id]; bounds.Offset(dx, dy); proposed[id] = bounds; }
        }
        var moves = ids.Select(id =>
        {
            var bounds = proposed[id];
            var screen = Screen.FromRectangle(bounds);
            int dpi = MonitorDpi(screen, _forms[id].DeviceDpi);
            bounds = ClampRootBounds(bounds, screen.WorkingArea, dpi);
            var layout = LayoutAt(id, bounds, screen, dpi);
            bounds = RestoreRootBounds(layout, screen.WorkingArea, dpi);
            var panel = StickerTreeLayout.PlanViewport(bounds, screen.WorkingArea, VisibleSubtreeContentHeight(id, dpi), dpi);
            return new SelectionMovePreview(id, layout, bounds, panel?.Bounds);
        }).ToArray();
        var anchor = moves.Single(move => move.BookmarkId == source.Bookmark.Id);
        return new(true, allRoots ? null : new BookmarkDropRequest(source.Bookmark.Id, BookmarkDropKind.Detach,
                BookmarkIds: ids, RootLayouts: moves.Select(move => move.Layout).ToArray()),
            anchor.Bounds, allRoots ? $"선택한 {ids.Count}개 이동" : $"선택한 {ids.Count}개를 최상위로 꺼내기",
            allRoots, anchor.PanelBounds, decision.Dpi, moves);
    }

    private static bool SameDropPreview(DropPreview first, DropPreview second)
    {
        if (first.Bounds != second.Bounds || first.Valid != second.Valid || first.IsRootMove != second.IsRootMove ||
            first.PanelBounds != second.PanelBounds || first.Dpi != second.Dpi || first.Label != second.Label ||
            !(first.Moves ?? []).SequenceEqual(second.Moves ?? [])) return false;
        if (first.Request is not { } a) return second.Request is null;
        return second.Request is { } b && a.BookmarkId == b.BookmarkId && a.Kind == b.Kind && a.TargetId == b.TargetId &&
            a.BeforeId == b.BeforeId && a.RootLayout == b.RootLayout && a.SourceIds.SequenceEqual(b.SourceIds) &&
            (a.RootLayouts ?? []).SequenceEqual(b.RootLayouts ?? []);
    }

    private static void UpdateSelectionGhosts(DragSession drag, DropPreview preview)
    {
        var peers = preview.Moves?.Where(move => move.BookmarkId != drag.Source.Bookmark.Id).ToArray() ?? [];
        while (drag.SelectionGhosts.Count > peers.Length)
        {
            drag.SelectionGhosts[^1].Dispose();
            drag.SelectionGhosts.RemoveAt(drag.SelectionGhosts.Count - 1);
        }
        for (int index = 0; index < peers.Length; index++)
        {
            if (index == drag.SelectionGhosts.Count) drag.SelectionGhosts.Add(new StickerDragPreview());
            var move = peers[index];
            var ghost = drag.SelectionGhosts[index];
            ghost.SetPreview(move.PanelBounds is { } panel ? Rectangle.Union(move.Bounds, panel) : move.Bounds, "", preview.Valid);
            if (!ghost.Visible) ghost.Show();
        }
    }

    internal sealed record SelectionMovePreview(Guid BookmarkId, StickerLayout Layout, Rectangle Bounds, Rectangle? PanelBounds);
}
