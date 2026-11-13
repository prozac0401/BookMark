using System.Windows.Forms;
using WorkBookmark.App.UI;

namespace WorkBookmark.App;

internal sealed partial class StickerManager
{
    private readonly HashSet<Guid> _selectedBookmarkIds = [];
    private Guid? _selectionClickId;
    private bool _selectionClickControl;

    internal IReadOnlyCollection<Guid> SelectedBookmarkIds => _selectedBookmarkIds.ToArray();

    private void ConfigureSelection(StickerForm form)
    {
        form.SelectionPointerDownRequested += (source, modifiers) =>
        {
            _selectionClickId = source.Bookmark.Id;
            _selectionClickControl = (modifiers & Keys.Control) != 0;
            SelectBookmark(source, modifiers, preserveSelection: true);
        };
        form.SelectionPointerUpRequested += (source, dragged) =>
        {
            if (_selectionClickId != source.Bookmark.Id) return;
            _selectionClickId = null;
            if (!dragged && !_selectionClickControl) SelectBookmark(source, Keys.None);
        };
        form.SelectionClearRequested += () =>
        {
            if (_selectedBookmarkIds.Count == 0) return false;
            ClearSelection();
            return true;
        };
    }

    internal void SelectBookmark(StickerForm source, Keys modifiers, bool preserveSelection = false)
    {
        if (_disposed || source.IsDisposed || !_forms.ContainsKey(source.Bookmark.Id)) return;
        Guid id = source.Bookmark.Id;
        if ((modifiers & Keys.Control) != 0)
        {
            if (!_selectedBookmarkIds.Add(id)) _selectedBookmarkIds.Remove(id);
        }
        else if (!preserveSelection || !_selectedBookmarkIds.Contains(id))
        {
            _selectedBookmarkIds.Clear();
            _selectedBookmarkIds.Add(id);
        }
        UpdateSelectionPresentation();
    }

    internal void ClearSelection()
    {
        _selectionClickId = null;
        if (_selectedBookmarkIds.Count == 0) return;
        _selectedBookmarkIds.Clear();
        UpdateSelectionPresentation();
    }

    private void PruneSelection()
    {
        _selectedBookmarkIds.RemoveWhere(id => !_forms.TryGetValue(id, out var form) || form.IsDisposed || !form.Visible);
        if (_selectionClickId is Guid click && !_selectedBookmarkIds.Contains(click)) _selectionClickId = null;
        UpdateSelectionPresentation();
    }

    private void UpdateSelectionPresentation()
    {
        foreach (var (id, form) in _forms)
            if (!form.IsDisposed) form.SetSelection(_selectedBookmarkIds.Contains(id));
    }
}
