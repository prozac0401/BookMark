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
    private bool _enabled, _hidden, _disposed, _applying;
    private Guid? _busyBookmarkId;

    internal event Action<Bookmark>? ResumeRequested, NoteRequested, DeleteRequested, RelinkRequested;
    internal event Action? ShowListRequested, SettingsRequested, UndoRequested;
    internal IReadOnlyCollection<StickerForm> Forms => _forms.Values;

    internal StickerManager(Func<Task<(IReadOnlyList<Bookmark> Items, IReadOnlyList<StickerLayout> Layouts)>> load,
        Action<IReadOnlyList<StickerLayout>> save, Action<string> notify)
    {
        _load = load; _save = save; _notify = notify;
        _saveTimer.Tick += async (_, _) => await SavePendingAsync();
    }

    internal async Task SetEnabledAsync(bool enabled, bool activate = false)
    {
        _enabled = enabled;
        if (!enabled) { ++_loadVersion; HideAll(); return; }
        await ShowAllAsync(activate);
    }

    internal async Task ShowAllAsync(bool activate = true)
    {
        if (!_enabled || _disposed) return;
        _hidden = false;
        int version = ++_loadVersion, revision = _revision;
        try
        {
            var loaded = await _load();
            if (_disposed || !_enabled || version != _loadVersion) return;
            // A capture/delete accepted while the read was in flight must not be overwritten.
            if (revision != _revision) { await ShowAllAsync(activate); return; }
            foreach (var layout in loaded.Layouts) _layouts.TryAdd(layout.BookmarkId, layout);
            var active = loaded.Items.Where(item => item.DeletedAtUtc is null).ToArray();
            var identities = active.Select(item => item.Id).ToHashSet();
            foreach (var id in _forms.Keys.Where(id => !identities.Contains(id)).ToArray()) Remove(id);
            foreach (var item in active.Reverse()) Upsert(item);
            foreach (var item in active.Reverse())
            {
                var form = _forms[item.Id];
                EnsureOnScreen(form);
                form.Show();
                // Explicit reveal raises the windows without activating each one in turn.
                SetWindowPos(form.Handle, form.TopMost ? new nint(-1) : nint.Zero, 0, 0, 0, 0, 0x0013);
            }
            if (activate && active.Length > 0)
            {
                var form = _forms[active[0].Id];
                form.Activate();
                form.FocusResume();
            }
            if (activate && active.Length == 0) _notify("책갈피가 없습니다. 작업 창에서 저장 단축키를 눌러 주세요.");
        }
        catch { if (!_disposed) _notify("스티커를 불러오지 못했습니다. 저장된 책갈피는 유지됩니다."); }
    }

    internal void Upsert(Bookmark item)
    {
        if (_disposed) return;
        ++_revision;
        if (item.DeletedAtUtc is not null) { Remove(item.Id); return; }
        if (_forms.TryGetValue(item.Id, out var existing)) { existing.UpdateBookmark(item); return; }
        if (!_enabled) return;
        var form = new StickerForm(item);
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
                form.Bounds = DefaultBounds(screen.WorkingArea, form.Size, _forms.Count - 1);
            }
            form.SetBusy(item.Id == _busyBookmarkId);
            if (!_hidden) form.Show();
        }
        finally { _applying = false; }
        // Also persist automatically assigned initial positions before restart.
        Remember(form);
    }

    internal void Remove(Guid id)
    {
        ++_revision;
        if (!_forms.Remove(id, out var form)) return;
        Remember(form);
        form.Dispose();
    }

    internal void HideAll()
    {
        _hidden = true;
        ++_loadVersion;
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

    internal void EditNote(Bookmark bookmark, Func<string, Task> saveNote)
    {
        if (!_enabled || _disposed) return;
        Upsert(bookmark);
        if (!_forms.TryGetValue(bookmark.Id, out var form)) return;
        EnsureOnScreen(form);
        // A note request reveals only its sticker, preserving all other hidden views.
        form.Show();
        form.Activate();
        form.BeginNoteEdit(saveNote);
    }

    internal async Task ArrangeAsync()
    {
        await ShowAllAsync(false);
        if (_disposed || !_enabled) return;
        var screen = Screen.FromPoint(Cursor.Position);
        int index = 0;
        foreach (var form in _forms.Values.OrderByDescending(value => value.Bookmark.CaptureSequence))
        {
            form.Bounds = DefaultBounds(screen.WorkingArea, form.Size, index++);
            Remember(form);
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
        var screen = Screen.FromRectangle(form.Bounds);
        float scale = 96F / Math.Max(96, form.DeviceDpi);
        var size = form.ExpandedSize;
        var layout = new StickerLayout(form.Bookmark.Id, screen.DeviceName,
            (int)Math.Round((form.Left - screen.WorkingArea.Left) * scale),
            (int)Math.Round((form.Top - screen.WorkingArea.Top) * scale),
            Math.Max(1, (int)Math.Round(size.Width * scale)), Math.Max(1, (int)Math.Round(size.Height * scale)),
            form.IsCollapsed, form.TopMost);
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

    private static Rectangle DefaultBounds(Rectangle area, Size size, int index)
    {
        int columns = Math.Max(1, (area.Width - 32) / (size.Width + 16));
        int rows = Math.Max(1, (area.Height - 32) / (size.Height + 16));
        int page = index / (columns * rows), slot = index % (columns * rows);
        int left = area.Right - 16 - size.Width - (slot % columns) * (size.Width + 16) - (page % 6) * 20;
        int top = area.Top + 16 + (slot / columns) * (size.Height + 16) + (page % 6) * 20;
        return ClampBounds(new(left, top, size.Width, size.Height), area, new(240, 150));
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
