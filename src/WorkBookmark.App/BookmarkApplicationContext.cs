using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Windows.Forms;
using WorkBookmark.App.UI;
using WorkBookmark.Core;
using WorkBookmark.Windows;

namespace WorkBookmark.App;

public sealed class BookmarkApplicationContext : ApplicationContext
{
    private readonly IBookmarkRepository _repository;
    private readonly WorkerClient _worker;
    private readonly BrowserCaptureServer _browserServer;
    private readonly Func<WorkerRequest, CancellationToken, Task<WorkerResponse>> _captureWorker;
    private ResumeInputMonitor? _captureInput;
    private CancellationTokenSource? _captureCancellation;
    private readonly string _dataDirectory;
    private readonly object _repositoryLock = new();
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly Control _dispatcher = new();
    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _hotkeys;
    private readonly RecentForm _recent;
    private readonly ToolStripMenuItem _undoMenu;
    private readonly System.Windows.Forms.Timer _undoTimer = new() { Interval = 10000 };
    private UserSettings _settings;
    private ToastForm? _toast;
    private NoteForm? _note;
    private SettingsForm? _settingsForm;
    private TargetSnapshot? _beforeList;
    private Guid? _activeRequest, _undoId, _lastResumeId;
    private DateTimeOffset _undoUntil;
    private long _lastResumeTick;
    private bool _operationInFlight, _busyNotified, _exiting, _settingsInvalid;

    public BookmarkApplicationContext(IBookmarkRepository repository, WorkerClient worker, string dataDirectory)
        : this(repository, worker, dataDirectory, worker.RunAsync) { }

    internal BookmarkApplicationContext(IBookmarkRepository repository, WorkerClient worker, string dataDirectory,
        Func<WorkerRequest, CancellationToken, Task<WorkerResponse>> captureWorker)
    {
        _repository = repository; _worker = worker; _dataDirectory = dataDirectory; _captureWorker = captureWorker;
        _ = _dispatcher.Handle;
        try { _settings = UserSettings.Load(dataDirectory); }
        catch { _settings = UserSettings.Default; _settingsInvalid = true; }
        _hotkeys = new HotkeyWindow(_settings.CaptureHotkey, _settings.RecentHotkey);
        _hotkeys.Pressed += capture =>
        {
            if (capture)
            {
                _ = CaptureAsync();
            }
            else ShowRecent();
        };
        _recent = new RecentForm(query => ReadAsync(() => _repository.List(query)));
        _recent.ResumeRequested += ResumeFromList;
        _recent.NoteRequested += bookmark => _ = EditNoteAsync(bookmark.Id);
        _recent.DeleteRequested += bookmark => _ = DeleteAsync(bookmark);
        _recent.RelinkRequested += bookmark => _ = RelinkAsync(bookmark);
        _recent.CancelRequested += CancelOperation;
        var menu = new ContextMenuStrip();
        menu.Items.Add("최근 책갈피", null, (_, _) => ShowRecent());
        _undoMenu = new ToolStripMenuItem("삭제 되돌리기", null, (_, _) => _ = UndoAsync()) { Visible = false };
        menu.Items.Add(_undoMenu);
        menu.Items.Add("진행 중 요청 중단", null, (_, _) => CancelOperation());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("설정", null, (_, _) => ShowSettings());
        menu.Items.Add("종료", null, (_, _) => ExitThread());
        _tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "업무 책갈피 · 제한된 시험판", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowRecent();
        _undoTimer.Tick += (_, _) => { _undoTimer.Stop(); _undoId = null; _undoMenu.Visible = false; };
        _browserServer = new BrowserCaptureServer(CaptureBrowserAsync);
        _dispatcher.BeginInvoke((Action)(() => _ = ShowIntroductionAsync()));
    }

    private Task<T> ReadAsync<T>(Func<T> action) => Task.Run(() => { lock (_repositoryLock) return action(); });
    private Task WriteAsync(Action action) => Task.Run(() => { lock (_repositoryLock) action(); });
    private bool BeginOperation(Guid id)
    {
        if (_operationInFlight || _worker.IsBusy)
        {
            if (!_busyNotified) { Notify("요청을 처리 중입니다. 완료 후 다시 눌러 주세요."); _busyNotified = true; }
            return false;
        }
        _operationInFlight = true; _busyNotified = false; _activeRequest = id;
        _recent.SetBusy(true);
        return true;
    }
    private void EndOperation(Guid id)
    {
        if (_activeRequest != id) return;
        _activeRequest = null; _operationInFlight = false; _busyNotified = false;
        if (!_exiting) _recent.SetBusy(false);
    }
    private void NotifyCaptureFailure(ResultCode code)
    {
        DiagnosticLog.Write("capture_rejected", code);
        Notify(UiStyle.Result(code));
    }
    private async Task CaptureAsync()
    {
        var id = Guid.NewGuid();
        // The monitor starts before the snapshot and worker startup. A changed cell/selection
        // can keep the same HWND, focus and view, so window identity alone is insufficient.
        using var cancellation = new CancellationTokenSource();
        ResumeInputMonitor? input = null;
        try
        {
            input = new ResumeInputMonitor(cancellation.Cancel);
            TargetSnapshot snapshot = ForegroundSnapshot.Capture();
            if (!BeginOperation(id)) return;
            _captureInput = input;
            _captureCancellation = cancellation;
            var request = new WorkerRequest(1, id, Operation.Capture, DateTimeOffset.UtcNow.AddSeconds(3), Snapshot: snapshot);
            Task<WorkerResponse> pending = _captureWorker(request, cancellation.Token);
            Notify("현재 작업 위치를 확인하는 중…");
            WorkerResponse response = await pending;
            DiagnosticLog.Write("capture_result", response.Code);
            if (_exiting || _activeRequest != id) return;
            if (input.HasNewInput) { NotifyCaptureFailure(ResultCode.ContextChanged); return; }
            if (cancellation.IsCancellationRequested) { NotifyCaptureFailure(ResultCode.Cancelled); return; }
            if (DateTimeOffset.UtcNow >= request.DeadlineUtc) { NotifyCaptureFailure(ResultCode.CaptureTimedOut); return; }
            if (response.RequestId != id || response.ProtocolVersion != 1 || response.Code != ResultCode.Captured || response.Target is null)
            {
                Notify(UiStyle.Result(response.Code)); return;
            }
            // Accept the immutable observation on the UI thread, then stop watching. Input
            // during the subsequent DB write does not invalidate the already captured location.
            input.Dispose();
            if (input.HasNewInput) { NotifyCaptureFailure(ResultCode.ContextChanged); return; }
            if (cancellation.IsCancellationRequested) { NotifyCaptureFailure(ResultCode.Cancelled); return; }
            _captureInput = null;
            CaptureCommit commit = await ReadAsync(() => _repository.UpsertCapture(response.Target));
            if (_exiting) return;
            DiagnosticLog.Write("capture_commit", ResultCode.CaptureCommitted);
            string position = UiStyle.PositionLabel(commit.Bookmark.Target);
            string retained = commit.ExistingNotePreserved ? "\n이전 메모 유지" : "";
            string restored = commit.RestoredDeleted ? "\n지웠던 책갈피를 복원했습니다." : "";
            string snapshotNotice = commit.Bookmark.Target.Kind == TargetKind.NotepadSnapshot ? "\n내용과 선택 위치를 이 PC에 보관했습니다." : "";
            Notify($"책갈피를 남겼습니다 · {commit.Bookmark.DisplayName}{position}\n{commit.Bookmark.CapturedAtUtc.ToLocalTime():HH:mm}{retained}{restored}{snapshotNotice}", "메모", () => _ = EditNoteAsync(commit.Bookmark.Id));
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("capture", exception is BookmarkException known ? known.Code : ResultCode.PersistenceFailed);
            Notify(UiStyle.Result(exception is BookmarkException typed ? typed.Code : ResultCode.PersistenceFailed));
        }
        finally
        {
            input?.Dispose();
            if (ReferenceEquals(_captureInput, input)) _captureInput = null;
            if (ReferenceEquals(_captureCancellation, cancellation)) _captureCancellation = null;
            EndOperation(id);
        }
    }
    internal Task<BrowserCaptureResponse> CaptureBrowserAsync(BrowserCaptureRequest request, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<BrowserCaptureResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        async void CommitOnUi()
        {
            bool began = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_exiting) { completion.TrySetResult(BrowserProtocol.Failure(request.RequestId, "AppNotRunning", "앱이 종료 중입니다.")); return; }
                CapturedTarget target = BrowserProtocol.Validate(request);
                began = BeginOperation(request.RequestId);
                if (!began) { completion.TrySetResult(BrowserProtocol.Failure(request.RequestId, "AppBusy", "다른 요청이 끝난 뒤 다시 눌러 주세요.")); return; }
                cancellationToken.ThrowIfCancellationRequested();
                CaptureCommit commit = await ReadAsync(() => { cancellationToken.ThrowIfCancellationRequested(); return _repository.UpsertCapture(target); });
                DiagnosticLog.Write("browser_capture_commit", ResultCode.CaptureCommitted);
                // A notification failure must not change the outcome of a committed write.
                completion.TrySetResult(new(request.RequestId, true, "CaptureCommitted", "책갈피를 남겼습니다."));
                try { Notify($"책갈피를 남겼습니다 · {commit.Bookmark.DisplayName}" + (commit.ExistingNotePreserved ? "\n이전 메모 유지" : ""), "메모", () => _ = EditNoteAsync(commit.Bookmark.Id)); }
                catch { DiagnosticLog.Write("browser_notification", ResultCode.CaptureCommitted); }
            }
            catch (OperationCanceledException) { completion.TrySetResult(BrowserProtocol.Failure(request.RequestId, "OutcomeUnknown", "저장 결과를 확인하지 못했습니다. 최근 목록을 확인해 주세요.")); }
            catch (BookmarkException e) { completion.TrySetResult(BrowserProtocol.Failure(request.RequestId, e.Code.ToString(), UiStyle.Result(e.Code))); }
            catch { completion.TrySetResult(BrowserProtocol.Failure(request.RequestId, "PersistenceFailed", "책갈피를 저장하지 못했습니다.")); }
            finally { if (began) EndOperation(request.RequestId); }
        }
        try { _dispatcher.BeginInvoke((Action)CommitOnUi); }
        catch { completion.TrySetResult(BrowserProtocol.Failure(request.RequestId, "AppNotRunning", "앱을 먼저 실행해 주세요.")); }
        return completion.Task.WaitAsync(cancellationToken);
    }
    public void ShowRecent()
    {
        if (_exiting) return;
        if (_dispatcher.InvokeRequired) { _dispatcher.BeginInvoke((Action)ShowRecent); return; }
        if (!_recent.Visible) _beforeList = ForegroundSnapshot.Capture();
        _ = OpenRecentAsync();
    }
    private async Task OpenRecentAsync()
    {
        try
        {
            await _recent.OpenAsync();
            DiagnosticLog.Write("recent_open", _recent.Visible ? ResultCode.Validated : ResultCode.Cancelled);
        }
        catch
        {
            DiagnosticLog.Write("recent_open", ResultCode.InvalidRequest);
            Notify("최근 목록을 열지 못했습니다. 설정에서 진단 로그를 확인해 주세요.");
        }
    }
    private void ResumeFromList(Bookmark bookmark)
    {
        if (_operationInFlight || (_lastResumeId == bookmark.Id && Environment.TickCount64 - _lastResumeTick < 1000)) return;
        _lastResumeId = bookmark.Id; _lastResumeTick = Environment.TickCount64;
        _recent.Hide();
        // Run after the Enter/click message is consumed; the list itself is not an external target.
        _dispatcher.BeginInvoke((Action)(() =>
        {
            TargetSnapshot current = ForegroundSnapshot.Capture();
            TargetSnapshot? baseline = current.Hwnd != 0 && current.ProcessId != (uint)Environment.ProcessId ? current : _beforeList is null ? null : _beforeList with { InputTick = current.InputTick };
            _ = ResumeAsync(bookmark, baseline);
        }));
    }
    private async Task ResumeAsync(Bookmark bookmark, TargetSnapshot? baseline)
    {
        var id = Guid.NewGuid();
        if (!BeginOperation(id)) return;
        ResultCode result = ResultCode.ResumeOutcomeUnknown;
        bool recordingResult = false;
        try
        {
            var request = new WorkerRequest(1, id, Operation.Resume, DateTimeOffset.UtcNow.AddSeconds(15), baseline, bookmark.Target);
            using var input = new ResumeInputMonitor(() =>
            {
                if (!_exiting && !_dispatcher.IsDisposed) _dispatcher.BeginInvoke((Action)(() => { if (_activeRequest == id) _worker.Cancel(); }));
            });
            Task<WorkerResponse> pending = _worker.RunAsync(request);
            _ = ShowResumeProgressAsync(id);
            WorkerResponse response = await pending;
            if (_exiting || _activeRequest != id) return;
            result = response.RequestId == id && response.ProtocolVersion == 1 ? response.Code : ResultCode.ResumeOutcomeUnknown;
            recordingResult = true;
            await WriteAsync(() => _repository.RecordResume(bookmark.Id, result));
            var updated = await ReadAsync(() => _repository.Get(bookmark.Id));
            if (updated is not null && !_exiting) _recent.Replace(updated);
            if (!_exiting) Notify(UiStyle.Result(result));
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("resume", exception is BookmarkException known ? known.Code : ResultCode.PersistenceFailed);
            Notify(recordingResult ? UiStyle.Result(result) + "\n재개 기록을 저장하지 못했을 수 있습니다." : UiStyle.Result(exception is BookmarkException typed ? typed.Code : ResultCode.ResumeOutcomeUnknown));
        }
        finally { EndOperation(id); }
    }
    private async Task ShowResumeProgressAsync(Guid id)
    {
        await Task.Delay(1000);
        if (!_exiting && _activeRequest == id) { Notify("여는 중…", "요청 중단", CancelOperation); _recent.SetBusy(true, "여는 중… 요청 중단 후에도 이미 전달된 동작은 완료될 수 있습니다."); }
    }
    private void CancelOperation()
    {
        if (!_operationInFlight) return;
        _captureCancellation?.Cancel();
        _worker.Cancel();
        Notify("관찰 중단을 요청했습니다. 이미 전달된 열기·이동은 나중에 완료될 수 있습니다.");
    }
    private async Task EditNoteAsync(Guid id)
    {
        try
        {
            if (_note is { IsDisposed: false }) { _note.Activate(); return; }
            Bookmark? bookmark = await ReadAsync(() => _repository.Get(id));
            if (bookmark is null || bookmark.DeletedAtUtc is not null || _exiting) return;
            _recent.Hide();
            _note = new NoteForm(bookmark, async text =>
            {
                try
                {
                    await WriteAsync(() => _repository.UpdateNote(id, text));
                    var updated = await ReadAsync(() => _repository.Get(id));
                    if (updated is not null && !_exiting) _recent.Replace(updated);
                }
                catch { DiagnosticLog.Write("note", ResultCode.PersistenceFailed); throw; }
            });
            _note.Show(); _note.Activate();
        }
        catch { Notify("메모를 읽을 수 없습니다. 기존 기록은 유지됩니다."); }
    }
    private async Task DeleteAsync(Bookmark bookmark)
    {
        try
        {
            await WriteAsync(() => _repository.SoftDelete(bookmark.Id));
            if (_exiting) return;
            _recent.Remove(bookmark.Id);
            _undoId = bookmark.Id; _undoUntil = DateTimeOffset.UtcNow.AddSeconds(10); _undoMenu.Visible = true;
            _undoTimer.Stop(); _undoTimer.Start();
            Notify("책갈피를 목록에서 지웠습니다. 원본 파일은 그대로입니다.", "되돌리기 · 10초", () => _ = UndoAsync(), 10000);
        }
        catch { Notify("목록에서 지우지 못했습니다. 기존 기록은 유지됩니다."); }
    }
    private async Task UndoAsync()
    {
        Guid? id = _undoId;
        if (id is null || DateTimeOffset.UtcNow > _undoUntil) return;
        try
        {
            await WriteAsync(() => _repository.Restore(id.Value));
            _undoTimer.Stop(); _undoId = null; _undoMenu.Visible = false;
            // Explicit undo is an intentional list change; preserve the selected identity.
            if (_recent.Visible) await _recent.ReloadAsync();
            Notify("책갈피를 복원했습니다.");
        }
        catch { Notify("되돌리지 못했습니다. 남은 시간 안에 다시 시도해 주세요."); }
    }
    private async Task RelinkAsync(Bookmark bookmark)
    {
        if (bookmark.LastResumeResult != ResultCode.TargetUnavailable || bookmark.Target.Kind is TargetKind.WebPage or TargetKind.NotepadSnapshot || OfficeLocation.IsWebTarget(bookmark.Target) || _operationInFlight) return;
        _recent.Hide();
        string? path = null;
        if (bookmark.Target.Kind == TargetKind.Folder)
        {
            using var picker = new FolderBrowserDialog { Description = "새 폴더 위치를 선택하세요.", UseDescriptionForTitle = true, ShowNewFolderButton = false };
            if (picker.ShowDialog() == DialogResult.OK) path = picker.SelectedPath;
        }
        else
        {
            using var picker = new OpenFileDialog { Title = "새 파일 위치를 선택하세요.", CheckFileExists = false, Multiselect = false, Filter = bookmark.Target.Kind == TargetKind.ExcelCell ? "Excel 통합문서|*.xlsx;*.xlsm;*.xlsb;*.xls" : "모든 파일|*.*" };
            if (picker.ShowDialog() == DialogResult.OK) path = picker.FileName;
        }
        if (path is null) return;
        var candidate = bookmark.Target with { Path = path };
        var id = Guid.NewGuid();
        if (!BeginOperation(id)) return;
        try
        {
            Notify("새 경로와 기존 위치를 확인하는 중…");
            var response = await _worker.RunAsync(new WorkerRequest(1, id, Operation.ValidateRelink, DateTimeOffset.UtcNow.AddSeconds(15), Target: candidate));
            if (_exiting || _activeRequest != id) return;
            if (response.RequestId != id || response.ProtocolVersion != 1 || response.Code != ResultCode.Validated)
            {
                Notify(UiStyle.Result(response.Code) + (candidate.Kind == TargetKind.ExcelCell ? "\n기존 시트·셀을 유지할 수 없으면 원하는 위치에서 새 책갈피를 남겨 주세요." : "")); return;
            }
            using var preview = new RelinkPreviewForm(bookmark, candidate);
            if (preview.ShowDialog() != DialogResult.OK) return;
            await WriteAsync(() => _repository.Relink(bookmark.Id, candidate));
            var updated = await ReadAsync(() => _repository.Get(bookmark.Id));
            if (updated is not null) _recent.Replace(updated);
            Notify("책갈피 경로를 변경했습니다. 메모와 저장 위치는 유지했습니다.");
        }
        catch (Exception exception) { Notify(UiStyle.Result(exception is BookmarkException known ? known.Code : ResultCode.PersistenceFailed)); }
        finally { EndOperation(id); }
    }
    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false }) { _settingsForm.Activate(); return; }
        _recent.Hide();
        _settingsForm = new SettingsForm(_settings, _hotkeys.CaptureRegistered, _hotkeys.RecentRegistered, _dataDirectory, ApplySettingsAsync,
            () => _ = OpenFolderAsync(_dataDirectory), () => _ = OpenFolderAsync(_dataDirectory));
        _settingsForm.Show(); _settingsForm.Activate();
    }
    private async Task<string?> ApplySettingsAsync(UserSettings requested)
    {
        await _settingsGate.WaitAsync();
        try { return await ApplySettingsCoreAsync(requested); }
        finally { _settingsGate.Release(); }
    }
    private async Task<string?> ApplySettingsCoreAsync(UserSettings requested)
    {
        UserSettings previous = _settings;
        if (!_hotkeys.TryUpdate(requested.CaptureHotkey, requested.RecentHotkey, out string error)) return error;
        try
        {
            await Task.Run(() =>
            {
                StartupRegistration.SetEnabled(requested.StartWithWindows);
                requested.Save(_dataDirectory);
            });
            _settings = requested; _settingsInvalid = false;
            return null;
        }
        catch
        {
            _hotkeys.TryUpdate(previous.CaptureHotkey, previous.RecentHotkey, out _);
            try { if (requested.StartWithWindows != previous.StartWithWindows) await Task.Run(() => StartupRegistration.SetEnabled(previous.StartWithWindows)); } catch { }
            return "설정 또는 시작프로그램을 저장하지 못했습니다. 기존 단축키 복원을 시도했습니다.";
        }
    }
    private async Task OpenFolderAsync(string path)
    {
        // Diagnostic folders are local, but Shell requests still use the isolated worker.
        try { await Task.Run(() => Directory.CreateDirectory(path)); }
        catch { Notify("폴더를 준비하지 못했습니다."); return; }
        var id = Guid.NewGuid();
        if (!BeginOperation(id)) return;
        try
        {
            var response = await _worker.RunAsync(new WorkerRequest(1, id, Operation.Resume, DateTimeOffset.UtcNow.AddSeconds(15), Target: new CapturedTarget(TargetKind.Folder, path)));
            if (response.Code != ResultCode.OpenRequested) Notify(UiStyle.Result(response.Code));
        }
        catch { Notify("폴더 열기를 요청하지 못했습니다."); }
        finally { EndOperation(id); }
    }
    private async Task ShowIntroductionAsync()
    {
        if (_settingsInvalid) { Notify("설정을 읽을 수 없어 기본 단축키를 사용합니다. 설정 파일 원본은 유지됩니다.", "설정", ShowSettings, 10000); return; }
        if (!_hotkeys.CaptureRegistered || !_hotkeys.RecentRegistered)
        {
            string unavailable = (!_hotkeys.CaptureRegistered ? $"저장 {_settings.CaptureHotkey} 비활성. " : "") + (!_hotkeys.RecentRegistered ? $"최근 {_settings.RecentHotkey} 비활성." : "");
            Notify("다른 앱과 단축키가 충돌했거나 등록할 수 없습니다.\n" + unavailable, "단축키 설정", ShowSettings, 10000);
        }
        else if (!_settings.IntroShown)
            Notify($"업무 책갈피 · 제한된 시험판\n{_settings.CaptureHotkey} 저장 · {_settings.RecentHotkey} 최근 목록\n탐색기 · Excel · Word · PowerPoint · 메모장\nEdge·Chrome은 확장 버튼에서 저장·단축키 확인", "설정", ShowSettings, 10000);
        if (!_settings.IntroShown)
        {
            await _settingsGate.WaitAsync();
            try
            {
                if (!_settings.IntroShown)
                {
                    var updated = _settings with { IntroShown = true };
                    await Task.Run(() => updated.Save(_dataDirectory)); _settings = updated;
                }
            }
            catch { }
            finally { _settingsGate.Release(); }
        }
    }
    private void Notify(string text, string? actionText = null, Action? action = null, int duration = 3000)
    {
        if (_exiting) return;
        _toast?.Close();
        _toast = new ToastForm(text, actionText, action, duration); _toast.Show();
    }
    protected override void ExitThreadCore()
    {
        if (_exiting) return;
        _exiting = true; _browserServer.Dispose(); _captureInput?.Dispose(); _worker.Cancel(); StartupRegistration.StopWorker();
        _tray.Visible = false; _hotkeys.Dispose(); _undoTimer.Dispose();
        _toast?.Close(); _note?.Dispose(); _settingsForm?.Dispose(); _recent.Dispose();
        _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); _dispatcher.Dispose();
        base.ExitThreadCore();
    }
}


/// <summary>Observes new input after the initiating UI message. No key/mouse payload is read or retained.</summary>
internal sealed class ResumeInputMonitor : IDisposable
{
    private readonly HookProc _keyboardProcedure, _mouseProcedure;
    private readonly Action _cancel;
    private IntPtr _keyboard, _mouse;
    private bool _notified, _disposed;
    public bool HasNewInput => _notified;
    public ResumeInputMonitor(Action cancel)
    {
        _cancel = cancel;
        _keyboardProcedure = Keyboard;
        _mouseProcedure = Mouse;
        IntPtr module = GetModuleHandle(null);
        _keyboard = SetWindowsHookEx(13, _keyboardProcedure, module, 0);
        _mouse = SetWindowsHookEx(14, _mouseProcedure, module, 0);
        if (_keyboard == IntPtr.Zero || _mouse == IntPtr.Zero) { Dispose(); throw new BookmarkException(ResultCode.ContextChanged); }
    }
    private IntPtr Keyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt64() is 0x0100 or 0x0104) Notify();
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }
    private IntPtr Mouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt64() is 0x0201 or 0x0204 or 0x0207 or 0x020A or 0x020B or 0x020E) Notify();
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }
    private void Notify() { if (!_disposed && !_notified) { _notified = true; _cancel(); } }
    public void Dispose()
    {
        _disposed = true;
        if (_keyboard != IntPtr.Zero) { UnhookWindowsHookEx(_keyboard); _keyboard = IntPtr.Zero; }
        if (_mouse != IntPtr.Zero) { UnhookWindowsHookEx(_mouse); _mouse = IntPtr.Zero; }
    }
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int hook, HookProc procedure, IntPtr module, uint thread);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
