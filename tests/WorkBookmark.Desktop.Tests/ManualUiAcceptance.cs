using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using WorkBookmark.App;
using WorkBookmark.Core;
using WorkBookmark.Storage;

namespace WorkBookmark.Desktop.Tests;

// Hosts the actual application context with an isolated repository. It never
// sends input, invokes private UI handlers, changes IME state, or edits user data.
// The operator drives the real product windows and uses this panel for evidence
// and storage fault injection. Installed Program.Main remains a separate gate.
internal static class ManualUiAcceptance
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static void Run(string directory, string workerExecutable, bool noteRegressions = false)
    {
        ApplicationConfiguration.Initialize();
        string allowed = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "manual-ui"));
        string root = Path.GetFullPath(directory);
        if (!root.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Manual fixture must be under this checkout's artifacts/manual-ui directory.");
        string workerPath = Path.GetFullPath(workerExecutable);
        if (!File.Exists(workerPath) || !Path.GetFileName(workerPath).Equals("WorkBookmark.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Provide the published WorkBookmark executable as the real worker.");
        string appAssembly = typeof(BookmarkApplicationContext).Assembly.Location;
        string publishedAssembly = Path.ChangeExtension(workerPath, ".dll");
        if (Hash(appAssembly) != Hash(publishedAssembly))
            throw new InvalidOperationException("UI host must use the exact published WorkBookmark.dll.");
        string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        using var mutex = new Mutex(true, @"Local\WorkBookmark_" + user, out bool firstInstance);
        if (!firstInstance) throw new InvalidOperationException("Another WorkBookmark instance is active.");
        try
        {
            string marker = Path.Combine(root, ".owned-manual-ui");
            if (Directory.Exists(root) && (!File.Exists(marker) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0))
                throw new InvalidOperationException("Existing fixture directory is not owned by this harness.");
            Directory.CreateDirectory(root);
            File.WriteAllText(marker, "WorkBookmark manual UI fixture v1");
            string data = Path.Combine(root, "data");
            Directory.CreateDirectory(data);
            DiagnosticLog.Initialize(data);
            int rejectNotes = 0, noteAttempts = 0;
            using var repository = new SqliteBookmarkRepository(Path.Combine(data, "bookmarks.db"), operation =>
            {
                if (operation != "note") return;
                Interlocked.Increment(ref noteAttempts);
                if (Volatile.Read(ref rejectNotes) != 0) throw new IOException("Owned manual UI fixture: injected note commit failure.");
            });
            using var gatedRepository = new ReadGateRepository(repository);
            string seedPath = Path.Combine(root, "fixture.json");
            if (noteRegressions && File.Exists(seedPath))
                throw new InvalidOperationException("Note regressions require a fresh fixture directory for reproducible bottom-edge placements.");
            Fixture fixture;
            if (File.Exists(seedPath)) fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(seedPath)) ?? throw new InvalidDataException("Invalid fixture metadata.");
            else
            {
                string target = Path.Combine(root, "합성 작업 문서.txt");
                File.WriteAllText(target, "BookMark synthetic UI acceptance. Preserve this source file.\r\n");
                string folder = Path.Combine(root, "합성 작업 폴더");
                Directory.CreateDirectory(folder);
                var first = repository.UpsertCapture(new CapturedTarget(TargetKind.File, target)).Bookmark;
                var second = repository.UpsertCapture(new CapturedTarget(TargetKind.Folder, folder)).Bookmark;
                repository.UpdateNote(first.Id, "입력 전 원본 메모");
                repository.UpdateNote(second.Id, "두 번째 스티커 원본");
                string monitor = Screen.PrimaryScreen?.DeviceName ?? "";
                if (noteRegressions)
                {
                    var area = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
                    using var dpiProbe = new Form { StartPosition = FormStartPosition.Manual, Location = area.Location };
                    _ = dpiProbe.Handle;
                    int logicalHeight = (int)Math.Round(area.Height * 96F / dpiProbe.DeviceDpi);
                    repository.SaveStickerLayout(new(first.Id, monitor, 80, logicalHeight - 68, 320, 60));
                    repository.SaveStickerLayout(new(second.Id, monitor, 440, logicalHeight - 56, 360, 100, IsCollapsed: true));
                }
                else
                {
                    repository.SaveStickerLayout(new(first.Id, monitor, 120, 130, 370, 360));
                    repository.SaveStickerLayout(new(second.Id, monitor, 560, 130, 370, 360));
                }
                fixture = new(first.Id, second.Id, target, folder, Hash(target));
                File.WriteAllText(seedPath, JsonSerializer.Serialize(fixture, Json));
                (UserSettings.Default with
                {
                    CaptureHotkey = new Hotkey(7, (int)Keys.F15), RecentHotkey = new Hotkey(7, (int)Keys.F16),
                    IntroShown = true, DisplayMode = BookmarkDisplayMode.Stickers,
                    StickerPresentationVersion = noteRegressions ? UserSettings.CurrentStickerPresentationVersion : 0
                }).Save(data);
            }
            using var worker = new WorkerClient(workerPath);
            using var context = new BookmarkApplicationContext(gatedRepository, worker, data);
            using var panel = new Form
            {
                Text = "BookMark 합성 실기 제어", StartPosition = FormStartPosition.Manual,
                Location = new Point(1020, 160), ClientSize = new Size(510, noteRegressions ? 390 : 280)
            };
            var instruction = new Label { Text = "실제 제품 창을 직접 조작하세요.\n이 창은 합성 저장소와 실패 주입만 관리합니다.", Location = new Point(18, 16), AutoSize = true };
            var reject = new CheckBox { Text = "메모 저장 실패 주입 (시험용)", Location = new Point(18, 72), AutoSize = true };
            reject.CheckedChanged += (_, _) => Volatile.Write(ref rejectNotes, reject.Checked ? 1 : 0);
            var other = new TextBox { Text = "창 전환 대상 — 여기에 포커스를 두고 자동 저장 확인", Location = new Point(18, 110), Width = 470 };
            var snapshot = new Button { Text = "저장소 스냅샷 기록", Location = new Point(18, 155), Size = new Size(175, 36) };
            var exit = new Button { Text = "정상 종료", Location = new Point(210, 155), Size = new Size(130, 36) };
            var status = new Label { Text = "준비됨", Location = new Point(18, 210), AutoSize = true };
            var armRead = new Button { Text = "다음 메모 읽기 대기", Location = new Point(18, 250), Size = new Size(190, 36) };
            var releaseRead = new Button { Text = "대기 중 읽기 완료", Location = new Point(225, 250), Size = new Size(190, 36) };
            var readStatus = new Label { Text = "읽기 대기 꺼짐", Location = new Point(18, 300), AutoSize = true };
            var hotkeyHint = new Label { Text = "전체 숨김/표시: Ctrl+Alt+Shift+F16\n정렬: 실제 트레이 → 스티커 위치 모으기", Location = new Point(18, 327), AutoSize = true };
            armRead.Click += (_, _) => gatedRepository.Arm();
            releaseRead.Click += (_, _) => gatedRepository.Release();
            string productHash = Hash(appAssembly);
            object ReadState(string stage)
            {
                var ids = new[] { fixture.First, fixture.Second };
                nint foreground = GetForegroundWindow();
                GetWindowThreadProcessId(foreground, out uint foregroundPid);
                return new
                {
                    stage, atUtc = DateTimeOffset.UtcNow, pid = Environment.ProcessId,
                    productAssemblySha256 = productHash, noteAttempts = Volatile.Read(ref noteAttempts),
                    failureInjected = Volatile.Read(ref rejectNotes) != 0,
                    gatedRepository.Armed, gatedRepository.Pending, gatedRepository.CompletedReads,
                    openingNote = typeof(BookmarkApplicationContext).GetField("_openingNote", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context),
                    foregroundWindow = foreground.ToInt64(), foregroundPid,
                    // Bypass the read gate and the application's repository lock:
                    // the pending Get is paused before entering SQLite, so evidence
                    // collection must not wait for the operator's release action.
                    bookmarks = ids.Select(id => repository.Get(id)).ToArray(),
                    layouts = repository.GetStickerLayouts().Where(layout => ids.Contains(layout.BookmarkId)).ToArray(),
                    sourceUnchanged = true, settings = UserSettings.Load(data),
                    stickerWindows = Application.OpenForms.Cast<Form>()
                        .Where(form => form.GetType().Name == "StickerForm")
                        .Select(form => new
                        {
                            id = ReadProperty<Bookmark>(form, "Bookmark").Id, form.Text, form.Visible, form.Bounds, form.DeviceDpi,
                            window = form.Handle.ToInt64(), isEditingNote = ReadProperty<bool>(form, "IsEditingNote"),
                            isCollapsed = ReadProperty<bool>(form, "IsCollapsed"),
                            placementBounds = ReadProperty<Rectangle>(form, "PlacementBounds"),
                            placementScreenBounds = ReadProperty<Rectangle>(form, "PlacementScreenBounds"),
                            expandedSize = ReadProperty<Size>(form, "ExpandedSize"),
                            placementIsCollapsed = ReadProperty<bool>(form, "PlacementIsCollapsed"),
                            draft = ((Control)form.GetType().GetField("_noteEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!).Text,
                            noteStatus = ((Control)form.GetType().GetField("_noteStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!).Text
                        }).ToArray()
                };
            }
            void Snapshot(string stage)
            {
                if (Hash(fixture.TargetFile) != fixture.TargetHash) throw new InvalidDataException("Synthetic source was modified.");
                string name = "snapshot-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + stage + ".json";
                File.WriteAllText(Path.Combine(root, name), JsonSerializer.Serialize(ReadState(stage), Json));
                status.Text = "스냅샷 기록: " + DateTime.Now.ToString("HH:mm:ss");
            }
            snapshot.Click += (_, _) => Snapshot("operator");
            exit.Click += (_, _) => { gatedRepository.Release(); context.ExitThread(); };
            panel.FormClosed += (_, _) => { gatedRepository.Release(); context.ExitThread(); };
            panel.Controls.AddRange([instruction, reject, other, snapshot, exit, status]);
            using var evidenceTimer = new System.Windows.Forms.Timer { Interval = 200 };
            if (noteRegressions)
            {
                panel.Controls.AddRange([armRead, releaseRead, readStatus, hotkeyHint]);
                // Observation only: the timer never releases the read or invokes
                // product actions. latest-state.json can be read without changing
                // focus by clicking the snapshot button during an editing flow.
                evidenceTimer.Tick += (_, _) =>
                {
                    readStatus.Text = gatedRepository.Pending ? "메모 DB 읽기 대기 중 — 전체 숨김 후 읽기 완료" : gatedRepository.Armed ? "다음 메모 요청을 기다림" : "읽기 완료 수: " + gatedRepository.CompletedReads;
                    armRead.Enabled = !gatedRepository.Armed && !gatedRepository.Pending;
                    releaseRead.Enabled = gatedRepository.Armed || gatedRepository.Pending;
                    File.WriteAllText(Path.Combine(root, "latest-state.json"), JsonSerializer.Serialize(ReadState("observed"), Json));
                };
                evidenceTimer.Start();
            }
            panel.Show();
            Snapshot("start");
            Application.Run(context);
            evidenceTimer.Stop();
            gatedRepository.Release();
            Snapshot("exit");
            panel.Close();
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static T ReadProperty<T>(object value, string property) =>
        (T)value.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(value)!;
    private sealed record Fixture(Guid First, Guid Second, string TargetFile, string TargetFolder, string TargetHash);

    private sealed class ReadGateRepository(IBookmarkRepository inner) : IBookmarkRepository
    {
        private readonly ManualResetEventSlim _release = new(true);
        private int _armed, _pending, _completedReads;
        internal bool Armed => Volatile.Read(ref _armed) != 0;
        internal bool Pending => Volatile.Read(ref _pending) != 0;
        internal int CompletedReads => Volatile.Read(ref _completedReads);
        internal void Arm()
        {
            if (Armed || Pending) return;
            _release.Reset();
            Volatile.Write(ref _armed, 1);
        }
        internal void Release() { Volatile.Write(ref _armed, 0); _release.Set(); }
        public Bookmark? Get(Guid id)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 0) return inner.Get(id);
            Volatile.Write(ref _pending, 1);
            try { _release.Wait(); return inner.Get(id); }
            finally { Interlocked.Increment(ref _completedReads); Volatile.Write(ref _pending, 0); }
        }
        public CaptureCommit UpsertCapture(CapturedTarget target) => inner.UpsertCapture(target);
        public SearchResults List(string query = "") => inner.List(query);
        public IReadOnlyList<Bookmark> ListActive() => inner.ListActive();
        public IReadOnlyList<Bookmark> ListDeleted(int limit = 100) => inner.ListDeleted(limit);
        public IReadOnlyList<StickerLayout> GetStickerLayouts() => inner.GetStickerLayouts();
        public void SaveStickerLayout(StickerLayout layout) => inner.SaveStickerLayout(layout);
        public void UpdateNote(Guid id, string note) => inner.UpdateNote(id, note);
        public void SoftDelete(Guid id) => inner.SoftDelete(id);
        public void Restore(Guid id) => inner.Restore(id);
        public void RecordResume(Guid id, ResultCode result) => inner.RecordResume(id, result);
        public void Relink(Guid id, CapturedTarget target) => inner.Relink(id, target);
        // The outer fixture owns SQLite. Release waiters before disposing it.
        public void Dispose() => Release();
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
