using System.Diagnostics;
using System.Drawing;
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

    internal static void Run(string directory, string workerExecutable)
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
            string seedPath = Path.Combine(root, "fixture.json");
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
                repository.SaveStickerLayout(new(first.Id, monitor, 120, 130, 370, 360));
                repository.SaveStickerLayout(new(second.Id, monitor, 560, 130, 370, 360));
                fixture = new(first.Id, second.Id, target, folder, Hash(target));
                File.WriteAllText(seedPath, JsonSerializer.Serialize(fixture, Json));
                (UserSettings.Default with
                {
                    CaptureHotkey = new Hotkey(7, (int)Keys.F15), RecentHotkey = new Hotkey(7, (int)Keys.F16),
                    IntroShown = true, DisplayMode = BookmarkDisplayMode.Stickers
                }).Save(data);
            }
            using var worker = new WorkerClient(workerPath);
            using var context = new BookmarkApplicationContext(repository, worker, data);
            using var panel = new Form
            {
                Text = "BookMark 합성 실기 제어", StartPosition = FormStartPosition.Manual,
                Location = new Point(1020, 160), ClientSize = new Size(510, 280)
            };
            var instruction = new Label { Text = "실제 제품 창을 직접 조작하세요.\n이 창은 합성 저장소와 실패 주입만 관리합니다.", Location = new Point(18, 16), AutoSize = true };
            var reject = new CheckBox { Text = "메모 저장 실패 주입 (시험용)", Location = new Point(18, 72), AutoSize = true };
            reject.CheckedChanged += (_, _) => Volatile.Write(ref rejectNotes, reject.Checked ? 1 : 0);
            var other = new TextBox { Text = "창 전환 대상 — 여기에 포커스를 두고 자동 저장 확인", Location = new Point(18, 110), Width = 470 };
            var snapshot = new Button { Text = "저장소 스냅샷 기록", Location = new Point(18, 155), Size = new Size(175, 36) };
            var exit = new Button { Text = "정상 종료", Location = new Point(210, 155), Size = new Size(130, 36) };
            var status = new Label { Text = "준비됨", Location = new Point(18, 210), AutoSize = true };
            void Snapshot(string stage)
            {
                if (Hash(fixture.TargetFile) != fixture.TargetHash) throw new InvalidDataException("Synthetic source was modified.");
                var ids = new[] { fixture.First, fixture.Second };
                var result = new
                {
                    stage, atUtc = DateTimeOffset.UtcNow, pid = Environment.ProcessId,
                    productAssemblySha256 = Hash(appAssembly), noteAttempts = Volatile.Read(ref noteAttempts),
                    failureInjected = Volatile.Read(ref rejectNotes) != 0,
                    bookmarks = ids.Select(id => repository.Get(id)).ToArray(),
                    layouts = repository.GetStickerLayouts().Where(layout => ids.Contains(layout.BookmarkId)).ToArray(),
                    sourceUnchanged = true, settings = UserSettings.Load(data)
                };
                string name = "snapshot-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + stage + ".json";
                File.WriteAllText(Path.Combine(root, name), JsonSerializer.Serialize(result, Json));
                status.Text = "스냅샷 기록: " + DateTime.Now.ToString("HH:mm:ss");
            }
            snapshot.Click += (_, _) => Snapshot("operator");
            exit.Click += (_, _) => context.ExitThread();
            panel.FormClosed += (_, _) => context.ExitThread();
            panel.Controls.AddRange([instruction, reject, other, snapshot, exit, status]);
            panel.Show();
            Snapshot("start");
            Application.Run(context);
            Snapshot("exit");
            panel.Close();
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private sealed record Fixture(Guid First, Guid Second, string TargetFile, string TargetFolder, string TargetHash);
}
