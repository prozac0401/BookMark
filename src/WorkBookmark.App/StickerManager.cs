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

    internal event Action<Bookmark>? ResumeRequested, NoteRequested, DeleteRequested, RelinkRequested;
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
        if (_forms.TryGetValue(item.Id, out var existing)) { existing.UpdateBookmark(item); return; }
        if (!_enabled) return;
        var form = new StickerForm(item);
        form.AdjustMoveBounds = (bounds, modifiers) => SnapMove(form, bounds, modifiers);
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
                form.Bounds = DefaultBounds(screen.WorkingArea, form.DefaultExpandedSize, _forms.Count - 1, form.MinimumSize);
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
        ++HideVersion;
        _hidden = true;
        _showPending = false;
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

    private Rectangle SnapMove(StickerForm moving, Rectangle proposed, Keys modifiers)
    {
        if (!SnapEnabled || !_enabled || _disposed || _applying || (modifiers & Keys.Alt) != 0) return proposed;
        var screen = Screen.FromRectangle(proposed);
        var neighbors = _forms.Values.Where(form => form != moving && !form.IsDisposed && form.Visible &&
                Screen.FromControl(form).DeviceName == screen.DeviceName)
            .Select(form => form.Bounds).ToArray();
        return StickerSnap.Snap(proposed, neighbors, screen.WorkingArea, moving.DeviceDpi);
    }

    internal async Task ArrangeAsync(StickerArrangementMode mode = StickerArrangementMode.Grid)
    {
        if (!await ShowAllAsync(false) || _disposed || !_enabled || _hidden) return;
        var screen = Screen.FromPoint(Cursor.Position);
        var forms = _forms.Values.OrderByDescending(value => value.Bookmark.CaptureSequence).ToArray();
        if (forms.Length == 0) return;
        // Moving to the destination monitor first lets WinForms apply that
        // monitor's DPI before measuring the row/column layout.
        foreach (var form in forms) form.Location = screen.WorkingArea.Location;
        var bounds = StickerArrangement.Arrange(forms.Select(form => form.Size).ToArray(),
            screen.WorkingArea, forms[0].DeviceDpi, mode);
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
            form.PlacementIsCollapsed, form.TopMost);
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
