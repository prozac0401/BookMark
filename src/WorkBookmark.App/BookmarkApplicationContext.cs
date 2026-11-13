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
    private readonly Icon _trayIcon;
    private readonly HotkeyWindow _hotkeys;
    private readonly RecentForm _recent;
    private readonly StickerManager _stickers;
    private readonly ToolStripMenuItem _hideStickersMenu, _arrangeStickersMenu;
    private readonly ToolStripMenuItem _undoMenu;
    private UserSettings _settings;
    private ToastForm? _toast;
    private NoteForm? _note;
    private Guid? _noteBookmarkId;
    private WebBookmarkForm? _webPage;
    private readonly Dictionary<(Guid? FolderId, Guid? ParentId), FolderNameForm> _folderNameEditors = [];
    private SettingsForm? _settingsForm;
    private DeletedBookmarksForm? _deletedForm;
    private TargetSnapshot? _beforeList;
    private Guid? _activeRequest, _lastResumeId;
    private readonly HashSet<Guid> _mutatingBookmarks = [];
    private readonly Dictionary<Guid, long> _bookmarkRefreshVersions = [];
    private long _bookmarkRefreshVersion;
    private long _lastResumeTick;
    private bool _operationInFlight, _busyNotified, _exiting, _settingsInvalid;
    private bool _openingNote;
    private bool _structureCommandPending, _exitRequested, _exitApproved;
    private bool _canUndo, _undoMenuRefreshPending;
    private long _undoMenuVersion;
    private int _openingNoteHideVersion;

    public BookmarkApplicationContext(IBookmarkRepository repository, WorkerClient worker, string dataDirectory)
        : this(repository, worker, dataDirectory, worker.RunAsync) { }

    internal BookmarkApplicationContext(IBookmarkRepository repository, WorkerClient worker, string dataDirectory,
        Func<WorkerRequest, CancellationToken, Task<WorkerResponse>> captureWorker)
    {
        _repository = repository; _worker = worker; _dataDirectory = dataDirectory; _captureWorker = captureWorker;
        _ = _dispatcher.Handle;
        try { _settings = UserSettings.Load(dataDirectory) with { StartWithWindows = StartupRegistration.IsEnabled }; }
        catch { _settings = UserSettings.Default; _settingsInvalid = true; }
        _hotkeys = new HotkeyWindow(_settings.CaptureHotkey, _settings.RecentHotkey);
        _hotkeys.Pressed += capture =>
        {
            if (capture)
            {
                _ = CaptureAsync();
            }
            else ToggleBookmarkVisibility();
        };
        // Load the complete graph so older parents and children remain reachable
        // and search can retain the ancestors of a matching bookmark.
        _recent = new RecentForm(_ => ReadAsync(() => new SearchResults(_repository.ListActive(), false)));
        _recent.ResumeRequested += ResumeFromList;
        _recent.NoteRequested += bookmark => _ = EditNoteAsync(bookmark.Id);
        _recent.DeleteRequested += bookmark => _ = DeleteAsync(bookmark);
        _recent.RelinkRequested += bookmark => _ = RelinkAsync(bookmark);
        _recent.ParentChangeRequested += (bookmark, parentId) => _ = ChangeParentAsync(bookmark, parentId);
        _recent.DropRequested += request => _ = ApplyDropAsync(request);
        _recent.UndoRequested += () => _ = UndoAsync();
        _recent.ColorChangeRequested += (bookmark, color) => _ = ChangeColorAsync(bookmark, color);
        _recent.FolderToggleRequested += bookmark => _ = ToggleFolderAsync(bookmark);
        _recent.CreateFolderRequested += parentId => ShowFolderName(parentId);
        _recent.RenameFolderRequested += bookmark => ShowFolderName(null, bookmark);
        _recent.SortChildrenRequested += (bookmark, mode) => _ = SortFolderAsync(bookmark, mode);
        _recent.ShowStickerRequested += bookmark => _ = ShowStickerAsync(bookmark);
        _recent.CancelRequested += CancelOperation;
        _stickers = new StickerManager(
            () => ReadAsync(() => (_repository.ListActive(), _repository.GetStickerLayouts())),
            layouts => { lock (_repositoryLock) _repository.SaveStickerLayouts(layouts); },
            message => Notify(message)) { SnapEnabled = _settings.StickerSnapEnabled };
        _stickers.ConfigureInitialLayoutSave(layout => { lock (_repositoryLock) _repository.SaveStickerLayout(layout); });
        _stickers.ResumeRequested += ResumeFromSticker;
        _stickers.NoteRequested += bookmark => _ = EditNoteAsync(bookmark.Id);
        _stickers.DeleteRequested += bookmark => _ = DeleteAsync(bookmark);
        _stickers.RelinkRequested += bookmark => _ = RelinkAsync(bookmark);
        _stickers.ParentChangeRequested += (bookmark, parentId) => _ = ChangeParentAsync(bookmark, parentId);
        _stickers.DropRequested += request => _ = ApplyDropAsync(request);
        _stickers.ColorChangeRequested += (bookmark, color) => _ = ChangeColorAsync(bookmark, color);
        _stickers.FolderToggleRequested += bookmark => _ = ToggleFolderAsync(bookmark);
        _stickers.CreateFolderRequested += parentId => ShowFolderName(parentId);
        _stickers.RenameFolderRequested += bookmark => _ = EditNoteAsync(bookmark.Id);
        _stickers.SortChildrenRequested += (bookmark, mode) => _ = SortFolderAsync(bookmark, mode);
        _stickers.ShowListRequested += ShowRecent;
        _stickers.SettingsRequested += ShowSettings;
        _stickers.UndoRequested += () => _ = UndoAsync();
        var menu = new ContextMenuStrip();
        menu.Items.Add("책갈피 보기", null, (_, _) => ShowBookmarks());
        menu.Items.Add("목록에서 찾기", null, (_, _) => ShowRecent());
        _hideStickersMenu = new ToolStripMenuItem("스티커 모두 숨기기", null, (_, _) => _ = _stickers.HideAllAsync());
        _arrangeStickersMenu = new ToolStripMenuItem("스티커 위치 모으기");
        _arrangeStickersMenu.DropDownItems.Add("격자로 정렬", null, (_, _) => _ = _stickers.ArrangeAsync(StickerArrangementMode.Grid));
        _arrangeStickersMenu.DropDownItems.Add("가로로 정렬", null, (_, _) => _ = _stickers.ArrangeAsync(StickerArrangementMode.Horizontal));
        _arrangeStickersMenu.DropDownItems.Add("세로로 정렬", null, (_, _) => _ = _stickers.ArrangeAsync(StickerArrangementMode.Vertical));
        menu.Items.Add(_hideStickersMenu); menu.Items.Add(_arrangeStickersMenu);
        menu.Items.Add("웹페이지 URL로 추가…", null, (_, _) => ShowWebBookmark());
        menu.Items.Add("새 폴더…", null, (_, _) => ShowFolderName(null));
        _undoMenu = new ToolStripMenuItem("마지막 변경 되돌리기", null, (_, _) => _ = UndoAsync()) { Enabled = false };
        menu.Items.Add(_undoMenu);
        menu.Opening += (_, _) => UpdateUndoMenu();
        menu.Items.Add("최근 삭제…", null, (_, _) => ShowDeleted());
        menu.Items.Add("진행 중 요청 중단", null, (_, _) => CancelOperation());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("설정", null, (_, _) => ShowSettings());
        menu.Items.Add("종료", null, (_, _) => ExitThread());
        _trayIcon = Branding.CreateTrayIcon();
        _tray = new NotifyIcon { Icon = _trayIcon, Text = "업무 책갈피", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowBookmarks();
        _browserServer = new BrowserCaptureServer(CaptureBrowserAsync);
        UpdateDisplayMenus();
        _dispatcher.BeginInvoke((Action)(async () =>
        {
            await InitializeDisplayAsync();
            if (!_exiting) await ShowIntroductionAsync();
        }));
    }

    private async Task InitializeDisplayAsync()
    {
        await _settingsGate.WaitAsync();
        try
        {
            if (_exiting) return;
            // Invalid settings are never rewritten by an automatic startup upgrade.
            if (!_settingsInvalid)
            {
                try
                {
                    _settings = await StickerPresentationUpgrade.ApplyAsync(_settings,
                        () => _stickers.ResetSavedSizesAsync(_settings.StickerPresentationVersion), updated =>
                        {
                            if (_exiting) throw new OperationCanceledException();
                            return Task.Run(() => updated.Save(_dataDirectory));
                        });
                }
                catch
                {
                    // Keep the saved mode usable for this session, but leave the marker
                    // incomplete so a later startup retries every saved layout.
                    if (!_exiting) Notify("스티커 표시 설정을 적용하지 못했습니다. 저장된 책갈피는 유지되며 다음 실행 때 다시 시도합니다.");
                }
            }
            if (_exiting) return;
            UpdateDisplayMenus();
            await _stickers.SetEnabledAsync(_settings.DisplayMode == BookmarkDisplayMode.Stickers);
        }
        catch
        {
            if (!_exiting) Notify("스티커 표시 설정을 적용하지 못했습니다. 저장된 책갈피는 유지되며 다음 실행 때 다시 시도합니다.");
        }
        finally { _settingsGate.Release(); }
    }

    private Task<T> ReadAsync<T>(Func<T> action) => Task.Run(() => { lock (_repositoryLock) return action(); });
    private Task WriteAsync(Action action) => Task.Run(() => { lock (_repositoryLock) action(); });
    private bool BeginOperation(Guid id, Guid? bookmarkId = null)
    {
        if (_exiting || _exitRequested) return false;
        if (_operationInFlight || _worker.IsBusy)
        {
            if (!_busyNotified) { Notify("요청을 처리 중입니다. 완료 후 다시 눌러 주세요."); _busyNotified = true; }
            return false;
        }
        _operationInFlight = true; _busyNotified = false; _activeRequest = id;
        _recent.SetBusy(true);
        _stickers.SetBusy(bookmarkId);
        return true;
    }
    private void EndOperation(Guid id)
    {
        if (_activeRequest != id) return;
        _activeRequest = null; _operationInFlight = false; _busyNotified = false;
        if (!_exiting) { _recent.SetBusy(false); _stickers.SetBusy(null); }
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
                if (response.Code is ResultCode.BrowserAddressUnavailable or ResultCode.BrowserExtensionRequired)
                    Notify(UiStyle.Result(response.Code), "URL 입력", ShowWebBookmark, 8000);
                else Notify(UiStyle.Result(response.Code));
                return;
            }
            // Accept the immutable observation on the UI thread, then stop watching. Input
            // during the subsequent DB write does not invalidate the already captured location.
            input.Dispose();
            if (input.HasNewInput) { NotifyCaptureFailure(ResultCode.ContextChanged); return; }
            if (cancellation.IsCancellationRequested) { NotifyCaptureFailure(ResultCode.Cancelled); return; }
            _captureInput = null;
            CaptureCommit commit = await ReadAsync(() => _repository.UpsertCapture(response.Target));
            if (_exiting) return;
            await RefreshBookmarkAsync(commit.Bookmark.Id);
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
                if (!_exiting) await RefreshBookmarkAsync(commit.Bookmark.Id);
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
        if (_exiting || _exitRequested) return;
        if (_dispatcher.InvokeRequired) { _dispatcher.BeginInvoke((Action)ShowRecent); return; }
        if (!_recent.Visible) _beforeList = ForegroundSnapshot.Capture();
        _ = OpenRecentAsync();
    }
    public void ShowBookmarks()
    {
        if (_exiting || _exitRequested) return;
        if (_dispatcher.InvokeRequired) { _dispatcher.BeginInvoke((Action)ShowBookmarks); return; }
        if (_settings.DisplayMode == BookmarkDisplayMode.Stickers) _ = _stickers.ShowAllAsync();
        else ShowRecent();
    }
    private void ToggleBookmarkVisibility()
    {
        if (_exiting || _exitRequested) return;
        if (_dispatcher.InvokeRequired) { _dispatcher.BeginInvoke((Action)ToggleBookmarkVisibility); return; }
        if (_settings.DisplayMode == BookmarkDisplayMode.Stickers) _ = _stickers.ToggleVisibilityAsync();
        else ShowRecent();
    }
    private void UpdateDisplayMenus()
    {
        bool stickers = _settings.DisplayMode == BookmarkDisplayMode.Stickers;
        _hideStickersMenu.Visible = stickers; _arrangeStickersMenu.Visible = stickers;
    }
    private void PublishBookmark(Bookmark bookmark)
    {
        if (_exiting) return;
        _bookmarkRefreshVersions[bookmark.Id] = ++_bookmarkRefreshVersion;
        try
        {
            if (bookmark.DeletedAtUtc is null)
            {
                _recent.Replace(bookmark);
            }
            else _recent.Remove(bookmark.Id);
            _stickers.Upsert(bookmark);
            UpdateUndoMenu();
        }
        catch
        {
            DiagnosticLog.Write("bookmark_view_refresh", ResultCode.PersistenceFailed);
            Notify("책갈피는 저장되었습니다. 보기를 새로 열어 변경 내용을 확인해 주세요.");
        }
    }
    private async Task<Bookmark?> RefreshBookmarkAsync(Guid id)
    {
        long version = ++_bookmarkRefreshVersion;
        _bookmarkRefreshVersions[id] = version;
        try
        {
            // A repository lock orders reads/writes, but their UI continuations can run
            // in another order. Only the newest requested read for this identity may
            // publish; every committed mutation rereads its current authoritative row.
            var bookmark = await ReadAsync(() => _repository.Get(id));
            if (_exiting || _bookmarkRefreshVersions[id] != version) return null;
            if (bookmark is not null) PublishBookmark(bookmark);
            else
            {
                _recent.Remove(id);
                _stickers.Remove(id);
            }
            return bookmark;
        }
        catch
        {
            if (!_exiting && _bookmarkRefreshVersions[id] == version)
            {
                DiagnosticLog.Write("bookmark_view_refresh", ResultCode.PersistenceFailed);
                Notify("책갈피는 저장되었습니다. 보기를 새로 열어 변경 내용을 확인해 주세요.");
            }
            return null;
        }
    }
    private async Task RefreshFolderChangesAsync(IEnumerable<Guid> identities, Guid? dropTargetId = null)
    {
        var versions = identities.Distinct().ToDictionary(id => id, _ => ++_bookmarkRefreshVersion);
        foreach (var entry in versions) _bookmarkRefreshVersions[entry.Key] = entry.Value;
        try
        {
            // A grouping transaction changes several records. Read and publish the
            // committed group together so neither view briefly invents a root item.
            var loaded = await ReadAsync(() => (Rows: versions.Keys.ToDictionary(id => id, id => _repository.Get(id)),
                Layouts: _repository.GetStickerLayouts().Where(layout => versions.ContainsKey(layout.BookmarkId)).ToArray()));
            if (_exiting) return;
            var current = new List<Bookmark>();
            foreach (var (id, row) in loaded.Rows)
            {
                if (_bookmarkRefreshVersions[id] != versions[id]) continue;
                if (row is not null) current.Add(row);
                else if ((_recent.FindBookmark(id) ?? _stickers.Forms.FirstOrDefault(form => form.Bookmark.Id == id)?.Bookmark) is { } removed)
                    current.Add(removed with { DeletedAtUtc = DateTimeOffset.UtcNow });
            }
            foreach (var row in current)
            {
                _bookmarkRefreshVersions[row.Id] = ++_bookmarkRefreshVersion;
            }
            _recent.ApplyFolderChanges(current);
            _stickers.ApplyCommittedFolderChanges(current,
                loaded.Layouts.Where(layout => current.Any(row => row.Id == layout.BookmarkId && row.DeletedAtUtc is null)).ToArray(), dropTargetId);
            UpdateUndoMenu();
        }
        catch
        {
            DiagnosticLog.Write("folder_view_refresh", ResultCode.PersistenceFailed);
            if (!_exiting) Notify("폴더는 저장되었습니다. 보기를 새로 열어 변경 내용을 확인해 주세요.");
        }
    }
    private void ShowFolderName(Guid? parentId, Bookmark? folder = null)
    {
        if (_exiting || _exitRequested) return;
        var key = (folder?.Id, folder is null ? parentId : null);
        if (_folderNameEditors.TryGetValue(key, out var existing) && !existing.IsDisposed)
        {
            existing.Activate();
            return;
        }
        var editor = new FolderNameForm(folder?.DisplayName ?? "새 폴더", folder is not null, async name =>
        {
            if (folder is null) await CreateFolderAsync(name, parentId);
            else await RenameFolderAsync(folder, name);
        });
        _folderNameEditors[key] = editor;
        editor.FormClosed += (_, _) => _folderNameEditors.Remove(key);
        ShowAuxiliary(editor);
    }
    private async Task<Bookmark> CreateFolderAsync(string name, Guid? parentId)
    {
        if (_exiting) throw new BookmarkException(ResultCode.Cancelled);
        var folder = await ReadAsync(() => _repository.CreateFolder(name, parentId));
        if (!_exiting) await RefreshFolderChangesAsync([folder.Id]);
        return folder;
    }
    private async Task RenameFolderAsync(Bookmark folder, string name)
    {
        if (_exiting || !folder.IsFolder || !_mutatingBookmarks.Add(folder.Id)) throw new BookmarkException(ResultCode.AppBusy);
        try
        {
            await WriteAsync(() => _repository.RenameFolder(folder.Id, name));
            if (!_exiting) await RefreshBookmarkAsync(folder.Id);
        }
        finally { _mutatingBookmarks.Remove(folder.Id); }
    }
    private async Task ToggleFolderAsync(Bookmark folder)
    {
        if (_exiting || _exitRequested || !folder.IsFolder || _structureCommandPending) return;
        _structureCommandPending = true;
        bool acquired = false;
        try
        {
            var before = await ReadAsync(() => _repository.Get(folder.Id));
            if (before is not { IsFolder: true, DeletedAtUtc: null }) return;
            if (before.FolderExpanded && !await CommitAffectedEditsAsync(folder.Id)) return;
            if (_exiting || !_mutatingBookmarks.Add(folder.Id)) return;
            acquired = true;
            await WriteAsync(() =>
            {
                var current = _repository.Get(folder.Id);
                if (current is not { IsFolder: true, DeletedAtUtc: null }) throw new BookmarkException(ResultCode.TargetUnavailable);
                _repository.SetFolderExpanded(current.Id, !before.FolderExpanded);
            });
            if (!_exiting) await RefreshBookmarkAsync(folder.Id);
        }
        catch { if (!_exiting) Notify("폴더의 열림 상태를 저장하지 못했습니다. 다시 눌러 주세요."); }
        finally { if (acquired) _mutatingBookmarks.Remove(folder.Id); _structureCommandPending = false; UpdateUndoMenu(); }
    }
    private void ShowWebBookmark()
    {
        if (_exiting || _exitRequested) return;
        if (_webPage is { IsDisposed: false }) { _webPage.Activate(); return; }
        _webPage = new WebBookmarkForm(async target =>
        {
            var id = Guid.NewGuid();
            if (!BeginOperation(id)) throw new BookmarkException(ResultCode.AppBusy);
            try
            {
                CaptureCommit commit = await ReadAsync(() => _repository.UpsertCapture(target));
                if (!_exiting) await RefreshBookmarkAsync(commit.Bookmark.Id);
                try { if (!_exiting) Notify($"책갈피를 남겼습니다 · {commit.Bookmark.DisplayName}", "메모", () => _ = EditNoteAsync(commit.Bookmark.Id)); }
                catch { DiagnosticLog.Write("manual_web_notification", ResultCode.CaptureCommitted); }
            }
            finally { EndOperation(id); }
        });
        ShowAuxiliary(_webPage);
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
        if (bookmark.IsFolder) { _ = ToggleFolderAsync(bookmark); return; }
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
    private void ResumeFromSticker(Bookmark bookmark)
    {
        if (bookmark.IsFolder) { _ = ToggleFolderAsync(bookmark); return; }
        if (_operationInFlight || (_lastResumeId == bookmark.Id && Environment.TickCount64 - _lastResumeTick < 1000)) return;
        _lastResumeId = bookmark.Id; _lastResumeTick = Environment.TickCount64;
        // The sticker remains alive. Use its current activation as the input baseline,
        // never the possibly stale window captured before a previous list opening.
        _dispatcher.BeginInvoke((Action)(() =>
        {
            if (!_exiting) _ = ResumeAsync(bookmark, ForegroundSnapshot.Capture());
        }));
    }
    private async Task ResumeAsync(Bookmark bookmark, TargetSnapshot? baseline)
    {
        if (bookmark.IsFolder) { await ToggleFolderAsync(bookmark); return; }
        var id = Guid.NewGuid();
        if (!BeginOperation(id, bookmark.Id)) return;
        ResultCode result = ResultCode.ResumeOutcomeUnknown;
        bool recordingResult = false;
        try
        {
            bool webOffice = OfficeLocation.IsWebTarget(bookmark.Target);
            using var inputSignal = ResumeInputSignal.Create(id);
            var request = new WorkerRequest(1, id, Operation.Resume,
                DateTimeOffset.UtcNow.AddSeconds(FrameProtocol.ResumeTimeoutSeconds(Operation.Resume, bookmark.Target)), baseline, bookmark.Target,
                MonitorInput: true);
            // Input stops later navigation/focus changes, not the requested open or its
            // read-only verification. Killing the worker here lost successful open results.
            using var input = new ResumeInputMonitor(() => { }, inputSignal);
            Task<WorkerResponse> pending = _worker.RunAsync(request);
            _ = ShowResumeProgressAsync(id, webOffice);
            WorkerResponse response = await pending;
            input.Dispose();
            if (_exiting || _activeRequest != id) return;
            result = response.RequestId == id && response.ProtocolVersion == 1 ? response.Code : ResultCode.ResumeOutcomeUnknown;
            recordingResult = true;
            await WriteAsync(() => _repository.RecordResume(bookmark.Id, result));
            if (!_exiting) await RefreshBookmarkAsync(bookmark.Id);
            if (!_exiting)
            {
                if (webOffice && result == ResultCode.OfficeResumePending)
                    Notify(UiStyle.Result(result), "사이트 로그인", () => _ = OpenOfficeSiteAsync(bookmark.Target), 10000);
                else NotifyResumeResult(result);
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("resume", exception is BookmarkException known ? known.Code : ResultCode.PersistenceFailed);
            if (recordingResult) Notify("재개 기록을 저장하지 못했을 수 있습니다.");
            else NotifyResumeResult(exception is BookmarkException typed ? typed.Code : ResultCode.ResumeOutcomeUnknown);
        }
        finally { EndOperation(id); }
    }
    private void NotifyResumeResult(ResultCode result)
    {
        // Preserve the actual result in history without interrupting work after Office
        // opens the document, or when the final outcome could not be confirmed.
        // Confirmed failures and login actions still have their own notifications.
        if (result is ResultCode.ResumeOutcomeUnknown or ResultCode.OfficeDocumentOpened)
        {
            _toast?.Close();
            _toast = null;
            return;
        }
        Notify(UiStyle.Result(result));
    }
    private async Task ShowResumeProgressAsync(Guid id, bool webOffice)
    {
        await Task.Delay(1000);
        if (!_exiting && _activeRequest == id)
        {
            string message = webOffice ? "웹 문서를 여는 중… 준비되는 동안 다른 작업을 계속할 수 있습니다." : "여는 중…";
            Notify(message, "요청 중단", CancelOperation, webOffice ? 10000 : 3000);
            _recent.SetBusy(true, "여는 중… 요청 중단 후에도 이미 전달된 동작은 완료될 수 있습니다.");
        }
    }
    private async Task OpenOfficeSiteAsync(CapturedTarget target)
    {
        var id = Guid.NewGuid();
        if (!BeginOperation(id)) return;
        try
        {
            var uri = new Uri(OfficeLocation.Normalize(target.Kind, target.Path));
            var site = new CapturedTarget(TargetKind.WebPage, uri.GetLeftPart(UriPartial.Authority) + "/", PageTitle: uri.Host);
            var response = await _worker.RunAsync(new(1, id, Operation.Resume, DateTimeOffset.UtcNow.AddSeconds(15), Target: site));
            Notify(response.Code == ResultCode.OpenRequested ? "사이트 열기를 요청했습니다. 로그인한 뒤 책갈피를 다시 실행해 주세요." : UiStyle.Result(response.Code));
        }
        catch { Notify("사이트 열기를 완료하지 못했습니다."); }
        finally { EndOperation(id); }
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
        if (_exiting || _exitRequested) return;
        int hideVersion = _stickers.HideVersion;
        if (_openingNote && _openingNoteHideVersion == hideVersion) return;
        _openingNote = true;
        _openingNoteHideVersion = hideVersion;
        try
        {
            if (_settings.DisplayMode != BookmarkDisplayMode.Stickers && _note is { IsDisposed: false }) { _note.Activate(); return; }
            // Versioned refresh prevents a delayed editor read from recreating a
            // sticker deleted or recaptured while that read was in flight.
            Bookmark? bookmark = await RefreshBookmarkAsync(id);
            // Hide cancels earlier opens, while a new request after hide remains valid.
            if (bookmark is null || bookmark.DeletedAtUtc is not null || _exiting || _exitRequested || hideVersion != _stickers.HideVersion) return;
            if (bookmark.IsFolder)
            {
                if (_settings.DisplayMode == BookmarkDisplayMode.Stickers &&
                    _stickers.RenameFolder(bookmark, name => RenameFolderAsync(bookmark, name)))
                {
                    _recent.Hide();
                    return;
                }
                ShowFolderName(null, bookmark);
                return;
            }
            _recent.Hide();
            async Task SaveNote(string text)
            {
                try
                {
                    await WriteAsync(() => _repository.UpdateNote(id, text));
                    if (!_exiting) await RefreshBookmarkAsync(id);
                }
                catch { DiagnosticLog.Write("note", ResultCode.PersistenceFailed); throw; }
            }
            if (_settings.DisplayMode == BookmarkDisplayMode.Stickers)
            {
                if (_stickers.EditNote(bookmark, SaveNote)) return;
            }
            _note = new NoteForm(bookmark, SaveNote);
            _noteBookmarkId = bookmark.Id;
            ShowAuxiliary(_note);
        }
        catch { Notify("편집할 책갈피를 읽을 수 없습니다. 기존 기록은 유지됩니다."); }
        finally { if (_openingNoteHideVersion == hideVersion) _openingNote = false; }
    }
    private async Task ChangeColorAsync(Bookmark bookmark, BookmarkColor color)
    {
        if (_exiting || _exitRequested || !_mutatingBookmarks.Add(bookmark.Id)) return;
        try
        {
            await WriteAsync(() => _repository.SetColor(bookmark.Id, color));
            if (!_exiting) await RefreshBookmarkAsync(bookmark.Id);
        }
        catch (Exception error)
        {
            DiagnosticLog.Write("bookmark_color", error is BookmarkException known ? known.Code : ResultCode.PersistenceFailed);
            const string message = "색상을 저장하지 못했습니다. 기존 색상은 유지됩니다. 다시 선택해 주세요.";
            if (!_exiting) _recent.SetBusy(_operationInFlight, message);
            Notify(message);
        }
        finally { _mutatingBookmarks.Remove(bookmark.Id); }
    }
    private async Task ChangeParentAsync(Bookmark bookmark, Guid? parentId)
    {
        if (_exiting || _exitRequested) return;
        BookmarkDropKind kind = BookmarkDropKind.Detach;
        if (parentId is Guid destination)
        {
            var target = await ReadAsync(() => _repository.Get(destination));
            kind = target?.IsFolder == true ? BookmarkDropKind.MoveInto : BookmarkDropKind.Group;
        }
        await ApplyDropAsync(new BookmarkDropRequest(bookmark.Id, kind, parentId));
    }
    private async Task ApplyDropAsync(BookmarkDropRequest request)
    {
        if (_exiting || _exitRequested || _structureCommandPending) return;
        _structureCommandPending = true;
        var locked = new List<Guid>();
        try
        {
            foreach (Guid id in request.SourceIds)
                if (!await CommitAffectedEditsAsync(id)) return;
            if (request.TargetId is Guid target && !await CommitAffectedEditsAsync(target)) return;
            if (_exiting) return;
            foreach (Guid id in request.SourceIds.Concat(new Guid?[] { request.TargetId, request.BeforeId }.OfType<Guid>()).Distinct())
            {
                if (!_mutatingBookmarks.Add(id)) return;
                locked.Add(id);
            }
            await _stickers.SavePendingAsync();
            var commit = await ReadAsync(() => _repository.ApplyDrop(request));
            if (_exiting) return;
            var changedIds = commit.ChangedItems.Select(item => item.Id)
                .Concat(request.RootLayouts?.Select(layout => layout.BookmarkId) ?? [])
                .Concat(request.Kind == BookmarkDropKind.Detach && request.RootLayout is not null ? [request.BookmarkId] : []);
            await RefreshFolderChangesAsync(changedIds, request.Kind == BookmarkDropKind.Group ? request.TargetId : null);
            if (commit.ChangedItems.Count == 0) return;
            if (request.Kind is BookmarkDropKind.Group or BookmarkDropKind.Detach)
                _stickers.RaiseTree(commit.CreatedFolder?.Id ?? request.BookmarkId);
            Notify(commit.CreatedFolder is not null ? "새 폴더로 묶었습니다. 우클릭으로 이름을 바꿀 수 있습니다."
                : request.Kind == BookmarkDropKind.InsertBefore ? "항목 순서를 변경했습니다."
                : request.Kind == BookmarkDropKind.Detach ? "최상위로 꺼냈습니다." : "폴더 안으로 옮겼습니다.");
        }
        catch (Exception error)
        {
            DiagnosticLog.Write("bookmark_parent", error is BookmarkException known ? known.Code : ResultCode.PersistenceFailed);
            string message = error is BookmarkException { Code: ResultCode.InvalidRequest }
                ? "자기 자신이나 하위 책갈피 아래로 옮길 수 없습니다."
                : error is BookmarkException { Code: ResultCode.TargetUnavailable }
                    ? "옮기려는 책갈피가 삭제되었습니다. 목록을 다시 열어 주세요."
                    : "책갈피 관계를 저장하지 못했습니다. 기존 관계는 유지됩니다. 다시 끌어 놓아 주세요.";
            if (!_exiting) _recent.SetBusy(_operationInFlight, message);
            Notify(message);
        }
        finally { foreach (Guid id in locked) _mutatingBookmarks.Remove(id); _structureCommandPending = false; UpdateUndoMenu(); }
    }
    private async Task DeleteAsync(Bookmark bookmark)
    {
        if (_exiting || _exitRequested || _structureCommandPending) return;
        _structureCommandPending = true;
        bool acquired = false;
        try
        {
            if (!await CommitAffectedEditsAsync(bookmark.Id)) return;
            if (_exiting || !_mutatingBookmarks.Add(bookmark.Id)) return;
            acquired = true;
            await _stickers.SavePendingAsync();
            await WriteAsync(() => _repository.SoftDelete(bookmark.Id));
            if (_exiting) return;
            var deleted = await RefreshBookmarkAsync(bookmark.Id);
            if (_exiting || deleted?.DeletedAtUtc is null) return;
            UpdateUndoMenu();
            Notify(bookmark.IsFolder ? "폴더를 지웠습니다. 안의 항목은 최상위에 남아 있습니다." : "책갈피를 목록에서 지웠습니다. 원본 파일은 그대로입니다.", "마지막 변경 되돌리기", () => _ = UndoAsync(), 10000);
            if (_deletedForm is { IsDisposed: false }) await _deletedForm.ReloadAsync();
        }
        catch { Notify("목록에서 지우지 못했습니다. 기존 기록은 유지됩니다."); }
        finally { if (acquired) _mutatingBookmarks.Remove(bookmark.Id); _structureCommandPending = false; UpdateUndoMenu(); }
    }
    private async Task SortFolderAsync(Bookmark folder, BookmarkSortMode mode)
    {
        if (_exiting || _exitRequested || !folder.IsFolder || _structureCommandPending) return;
        _structureCommandPending = true;
        try
        {
            if (!await CommitAffectedEditsAsync(folder.Id)) return;
            await _stickers.SavePendingAsync();
            var commit = await ReadAsync(() => _repository.SortChildren(folder.Id, mode));
            if (_exiting) return;
            await RefreshFolderChangesAsync(commit.ChangedItems.Select(item => item.Id));
            if (commit.ChangedItems.Count > 0) Notify(mode == BookmarkSortMode.Name ? "하위 항목을 이름순으로 정렬했습니다." : "하위 항목을 최근 저장순으로 정렬했습니다.");
        }
        catch { if (!_exiting) Notify("항목 순서를 저장하지 못했습니다. 기존 순서는 유지됩니다."); }
        finally { _structureCommandPending = false; UpdateUndoMenu(); }
    }
    private async Task ShowStickerAsync(Bookmark bookmark)
    {
        if (_exiting || _exitRequested || _structureCommandPending) return;
        _structureCommandPending = true;
        try
        {
            if (_settings.DisplayMode != BookmarkDisplayMode.Stickers)
            {
                string? error = await ApplySettingsAsync(_settings with { DisplayMode = BookmarkDisplayMode.Stickers });
                if (error is not null) { Notify(error); return; }
            }
            await _stickers.SavePendingAsync();
            var changed = await ReadAsync(() =>
            {
                var current = _repository.Get(bookmark.Id);
                if (current is null || current.DeletedAtUtc is not null) throw new BookmarkException(ResultCode.TargetUnavailable);
                var ids = new List<Guid> { current.Id };
                var seen = new HashSet<Guid> { current.Id };
                while (current.ParentId is Guid parent && seen.Add(parent))
                {
                    current = _repository.Get(parent);
                    if (current is null || current.DeletedAtUtc is not null) break;
                    if (current.IsFolder && !current.FolderExpanded) _repository.SetFolderExpanded(current.Id, true);
                    ids.Add(current.Id);
                }
                return ids;
            });
            if (_exiting) return;
            await RefreshFolderChangesAsync(changed);
            _recent.Hide();
            if (!_stickers.ShowItem(bookmark.Id)) Notify("항목을 표시하지 못했습니다. 책갈피 보기를 다시 열어 주세요.");
        }
        catch { if (!_exiting) Notify("항목을 표시하지 못했습니다. 기존 내용은 유지됩니다."); }
        finally { _structureCommandPending = false; UpdateUndoMenu(); }
    }
    private async Task UndoAsync()
    {
        if (_exiting || _exitRequested || _structureCommandPending) return;
        if (_stickers.HasPendingEdits || _note is { IsDisposed: false } || _folderNameEditors.Values.Any(editor => !editor.IsDisposed))
        {
            Notify("편집을 저장하거나 취소한 뒤 마지막 변경을 되돌려 주세요.");
            return;
        }
        _structureCommandPending = true;
        try
        {
            await _stickers.SavePendingAsync();
            var result = await ReadAsync(() => _repository.CanUndo ? _repository.Undo() : null);
            if (_exiting || result is null) return;
            await RefreshFolderChangesAsync(result.ChangedIds);
            if (_deletedForm is { IsDisposed: false }) await _deletedForm.ReloadAsync();
            Notify("마지막 변경을 되돌렸습니다.");
        }
        catch
        {
            Notify("변경을 되돌리지 못했습니다. 현재 내용은 유지됩니다.");
        }
        finally { _structureCommandPending = false; UpdateUndoMenu(); }
    }
    private void UpdateUndoMenu()
    {
        if (_exiting) return;
        long version = ++_undoMenuVersion;
        // Publishing a completed read must not block the UI behind another
        // repository operation, which may itself be waiting for UI input.
        if (Monitor.TryEnter(_repositoryLock))
        {
            try { _canUndo = _repository.CanUndo; }
            finally { Monitor.Exit(_repositoryLock); }
        }
        else if (!_undoMenuRefreshPending)
        {
            _undoMenuRefreshPending = true;
            _ = RefreshUndoMenuAsync(version);
        }
        _undoMenu.Enabled = !_structureCommandPending && _canUndo;
    }
    private async Task RefreshUndoMenuAsync(long version)
    {
        bool refreshAgain = false;
        try
        {
            bool canUndo = await ReadAsync(() => _repository.CanUndo);
            if (_exiting) return;
            if (version == _undoMenuVersion)
            {
                _canUndo = canUndo;
                _undoMenu.Enabled = !_structureCommandPending && _canUndo;
            }
            else refreshAgain = true;
        }
        catch { /* A later menu opening retries; keep the last known availability. */ }
        finally { _undoMenuRefreshPending = false; }
        if (refreshAgain && !_exiting) UpdateUndoMenu();
    }
    private async Task<bool> CommitAffectedEditsAsync(Guid subtreeId)
    {
        if (!_stickers.HasPendingEdits && _note is not { IsDisposed: false } &&
            !_folderNameEditors.Values.Any(editor => !editor.IsDisposed)) return true;
        var affected = await ReadAsync(() =>
        {
            // Include children whose deleted parent is about to be restored. The
            // visible tree projection temporarily presents those children as roots.
            var rows = _repository.ListActive();
            var ids = new HashSet<Guid> { subtreeId };
            bool added;
            do
            {
                added = false;
                foreach (var row in rows)
                    if (row.ParentId is Guid parent && ids.Contains(parent)) added |= ids.Add(row.Id);
            } while (added);
            return ids;
        });
        foreach (Guid id in affected)
            if (!await _stickers.CommitPendingEditsAsync(id)) return false;
        if (_note is { IsDisposed: false } note && _noteBookmarkId is Guid noteId && affected.Contains(noteId) &&
            !await note.CommitPendingEditAsync()) return false;
        foreach (var (key, editor) in _folderNameEditors.ToArray())
            if (key.FolderId is Guid folderId && affected.Contains(folderId) && !editor.IsDisposed &&
                !await editor.CommitPendingEditAsync()) return false;
        return true;
    }
    private async Task<bool> RestoreBookmarkAsync(Guid id)
    {
        if (_exiting || _exitRequested || _structureCommandPending) return false;
        _structureCommandPending = true;
        bool acquired = false;
        try
        {
            if (!await CommitAffectedEditsAsync(id)) return false;
            if (_exiting || !_mutatingBookmarks.Add(id)) return false;
            acquired = true;
            await _stickers.SavePendingAsync();
            await WriteAsync(() => _repository.Restore(id));
            if (_exiting) return true;
            var restored = await RefreshBookmarkAsync(id);
            if (_exiting) return true;
            // Explicit undo is an intentional list change; preserve the selected identity.
            if (_recent.Visible) await _recent.ReloadAsync();
            if (_deletedForm is { IsDisposed: false }) await _deletedForm.ReloadAsync();
            if (restored is { DeletedAtUtc: null })
            {
                UpdateUndoMenu();
                Notify("책갈피를 복원했습니다.");
            }
            return true;
        }
        catch { Notify("복원하지 못했습니다. ‘최근 삭제’에서 다시 시도할 수 있습니다."); return false; }
        finally { if (acquired) _mutatingBookmarks.Remove(id); _structureCommandPending = false; UpdateUndoMenu(); }
    }
    private void ShowDeleted()
    {
        if (_exiting || _exitRequested) return;
        if (_deletedForm is { IsDisposed: false }) { _deletedForm.Activate(); return; }
        _recent.Hide();
        _deletedForm = new DeletedBookmarksForm(() => ReadAsync(() => _repository.ListDeleted()), RestoreBookmarkAsync);
        ShowAuxiliary(_deletedForm);
    }
    private async Task RelinkAsync(Bookmark bookmark)
    {
        if (bookmark.IsFolder) return;
        if (bookmark.LastResumeResult != ResultCode.TargetUnavailable || bookmark.Target.Kind is TargetKind.WebPage or TargetKind.NotepadSnapshot || OfficeLocation.IsWebTarget(bookmark.Target) || _operationInFlight) return;
        _recent.Hide();
        string? path = null;
        if (bookmark.Target.Kind == TargetKind.Folder)
        {
            using var picker = new FolderBrowserDialog { Description = "새 폴더 위치를 선택하세요.", UseDescriptionForTitle = true, ShowNewFolderButton = false };
            if (picker.ShowDialog(DialogOwner()) == DialogResult.OK) path = picker.SelectedPath;
        }
        else
        {
            using var picker = new OpenFileDialog { Title = "새 파일 위치를 선택하세요.", CheckFileExists = false, Multiselect = false, Filter = bookmark.Target.Kind == TargetKind.ExcelCell ? "Excel 통합문서|*.xlsx;*.xlsm;*.xlsb;*.xls" : "모든 파일|*.*" };
            if (picker.ShowDialog(DialogOwner()) == DialogResult.OK) path = picker.FileName;
        }
        if (path is null) return;
        var candidate = bookmark.Target with { Path = path };
        var id = Guid.NewGuid();
        if (!BeginOperation(id, bookmark.Id)) return;
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
            if (preview.ShowDialog(DialogOwner()) != DialogResult.OK) return;
            await WriteAsync(() => _repository.Relink(bookmark.Id, candidate));
            if (!_exiting) await RefreshBookmarkAsync(bookmark.Id);
            Notify("책갈피 경로를 변경했습니다. 메모와 저장 위치는 유지했습니다.");
        }
        catch (Exception exception) { Notify(UiStyle.Result(exception is BookmarkException known ? known.Code : ResultCode.PersistenceFailed)); }
        finally { EndOperation(id); }
    }
    private void ShowSettings()
    {
        if (_exiting || _exitRequested) return;
        if (_settingsForm is { IsDisposed: false }) { _settingsForm.Activate(); return; }
        _recent.Hide();
        _settingsForm = new SettingsForm(_settings, _hotkeys.CaptureRegistered, _hotkeys.RecentRegistered, _dataDirectory, ApplySettingsAsync,
            () => _ = OpenFolderAsync(_dataDirectory), () => _ = OpenFolderAsync(_dataDirectory));
        ShowAuxiliary(_settingsForm);
    }
    private Form? DialogOwner() => Form.ActiveForm is StickerForm sticker ? sticker :
        _stickers.Forms.FirstOrDefault(form => form.Visible && form.TopMost);
    private void ShowAuxiliary(Form form)
    {
        // Modeless editors must outlive any individual sticker. Owning them with a
        // sticker would discard their unsaved input when that bookmark is deleted.
        form.TopMost = _stickers.Forms.Any(sticker => sticker.Visible && sticker.TopMost);
        form.Show();
        form.Activate();
    }
    private async Task<string?> ApplySettingsAsync(UserSettings requested)
    {
        await _settingsGate.WaitAsync();
        try
        {
            // A settings window opened while startup was saving may hold an older marker.
            requested = requested with { StickerPresentationVersion = _settings.StickerPresentationVersion };
            return await ApplySettingsCoreAsync(requested);
        }
        finally { _settingsGate.Release(); }
    }
    private async Task<string?> ApplySettingsCoreAsync(UserSettings requested)
    {
        if (!Enum.IsDefined(requested.DisplayMode)) return "책갈피 보기 방식을 다시 선택해 주세요.";
        UserSettings previous = _settings;
        if (previous.DisplayMode != requested.DisplayMode && !await _stickers.CommitPendingEditsAsync())
            return "편집 내용을 저장하지 못해 보기 방식을 변경하지 않았습니다. 편집을 저장하거나 취소해 주세요.";
        if (!_hotkeys.TryUpdate(requested.CaptureHotkey, requested.RecentHotkey, out string error)) return error;
        try
        {
            await Task.Run(() =>
            {
                StartupRegistration.SetEnabled(requested.StartWithWindows);
                requested.Save(_dataDirectory);
            });
            _settings = requested; _settingsInvalid = false;
            _stickers.SnapEnabled = _settings.StickerSnapEnabled;
            UpdateDisplayMenus();
            if (_settings.DisplayMode == BookmarkDisplayMode.Stickers) _recent.Hide();
            await _stickers.SetEnabledAsync(_settings.DisplayMode == BookmarkDisplayMode.Stickers);
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
            string unavailable = (!_hotkeys.CaptureRegistered ? $"저장 {_settings.CaptureHotkey} 비활성. " : "") + (!_hotkeys.RecentRegistered ? $"책갈피 보기 {_settings.RecentHotkey} 비활성." : "");
            Notify("다른 앱과 단축키가 충돌했거나 등록할 수 없습니다.\n" + unavailable, "단축키 설정", ShowSettings, 10000);
        }
        else if (!_settings.IntroShown)
            Notify($"업무 책갈피\n{_settings.CaptureHotkey} 저장 · {_settings.RecentHotkey} 책갈피 보기\n탐색기 · Excel · Word · PowerPoint · 메모장\nEdge·Chrome·웨일: 확장 없이 저장 · 트레이에서 URL 입력 가능", "설정", ShowSettings, 10000);
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
    private void ReleaseResources()
    {
        if (_exiting) return;
        _exiting = true; _browserServer.Dispose(); _captureInput?.Dispose(); _worker.Cancel(); StartupRegistration.StopWorker();
        _tray.Visible = false; _hotkeys.Dispose();
        _stickers.Dispose();
        _toast?.Close(); _note?.Dispose(); _webPage?.Dispose();
        foreach (var editor in _folderNameEditors.Values.ToArray()) editor.Dispose();
        _folderNameEditors.Clear();
        _settingsForm?.Dispose(); _deletedForm?.Dispose(); _recent.Dispose();
        _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); _trayIcon.Dispose(); _dispatcher.Dispose();
    }
    protected override void ExitThreadCore()
    {
        if (_exiting) return;
        if (!_exitApproved) { _ = RequestExitAsync(); return; }
        ReleaseResources();
        base.ExitThreadCore();
    }
    private async Task RequestExitAsync()
    {
        if (_exiting || _exitRequested) return;
        _exitRequested = true;
        try
        {
            // Stop accepting new editors/commands while the existing operation
            // and its save callbacks finish; otherwise exit could miss a new draft.
            while ((_structureCommandPending || _mutatingBookmarks.Count > 0) && !_exiting) await Task.Delay(20);
            if (_exiting) return;
            if (!await _stickers.CommitPendingEditsAsync()) return;
            if (_note is { IsDisposed: false } note && !await note.CommitPendingEditAsync()) return;
            foreach (var editor in _folderNameEditors.Values.ToArray())
                if (!editor.IsDisposed && !await editor.CommitPendingEditAsync()) return;
            await _stickers.SavePendingAsync();
            _exitApproved = true;
            ExitThread();
        }
        catch { Notify("저장하지 못한 내용이 있어 종료하지 않았습니다. 편집을 저장하거나 취소한 뒤 다시 종료해 주세요."); }
        finally { _exitRequested = false; }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) ReleaseResources();
        base.Dispose(disposing);
    }
}
