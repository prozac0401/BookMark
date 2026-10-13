using System.Collections.Concurrent;
using System.Drawing;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using WorkBookmark.App;
using WorkBookmark.Core;
using WorkBookmark.Storage;

namespace WorkBookmark.Desktop.Tests;

/// <summary>
/// A taskbar-visible operator panel surrounding unmodified product windows and
/// the real SQLite/application context. It sends no input and cannot perform a
/// tested drag, color selection, detach, or note edit on the operator's behalf.
/// </summary>
internal static class ManualTreeColorsAcceptance
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

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
        string productHash = Hash(typeof(BookmarkApplicationContext).Assembly.Location);
        if (productHash != Hash(Path.ChangeExtension(workerPath, ".dll")))
            throw new InvalidOperationException("UI host must use the exact published WorkBookmark.dll.");
        string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        using var mutex = new Mutex(true, @"Local\WorkBookmark_" + user, out bool firstInstance);
        if (!firstInstance) throw new InvalidOperationException("Another WorkBookmark instance is active.");
        try
        {
            string marker = Path.Combine(root, ".owned-manual-tree-colors");
            if (Directory.Exists(root) && (!File.Exists(marker) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0))
                throw new InvalidOperationException("Existing fixture directory is not owned by this harness.");
            Directory.CreateDirectory(root);
            File.WriteAllText(marker, "WorkBookmark isolated tree and color acceptance v1");
            string data = Path.Combine(root, "data");
            Directory.CreateDirectory(data);
            DiagnosticLog.Initialize(data);
            int rejectParents = 0, rejectColors = 0;
            var attempts = new ConcurrentQueue<CommitAttempt>();
            using var repository = new SqliteBookmarkRepository(Path.Combine(data, "bookmarks.db"), operation =>
            {
                if (operation is not ("parent" or "color")) return;
                bool rejected = operation == "parent" ? Volatile.Read(ref rejectParents) != 0 : Volatile.Read(ref rejectColors) != 0;
                attempts.Enqueue(new(DateTimeOffset.UtcNow, operation, rejected));
                if (rejected) throw new IOException("Owned manual fixture: injected " + operation + " commit failure.");
            });
            string fixturePath = Path.Combine(root, "fixture.json");
            Fixture fixture;
            if (File.Exists(fixturePath))
                fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(fixturePath), Json) ?? throw new InvalidDataException("Invalid fixture metadata.");
            else
            {
                var seeds = new List<Seed>();
                string[] labels = ["A · 신규 서비스 기획", "B · 시장 조사 자료", "C · 경쟁 서비스 비교"];
                for (int index = 0; index < labels.Length; index++)
                {
                    string path = Path.Combine(root, $"합성 문서 {(char)('A' + index)}.txt");
                    File.WriteAllText(path, "WorkBookmark synthetic acceptance source; preserve this file.\r\n" + labels[index]);
                    var bookmark = repository.UpsertCapture(new CapturedTarget(TargetKind.File, path)).Bookmark;
                    repository.UpdateNote(bookmark.Id, labels[index]);
                    seeds.Add(new(bookmark.Id, labels[index], path, Hash(path)));
                }
                var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
                // Saved placement is only fixture setup. Subsequent interactions
                // use the actual native window move loop and product event wiring.
                var managerType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.StickerManager")!;
                var size = (Size)managerType.GetProperty("DefaultExpandedSize96", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                for (int index = 0; index < seeds.Count; index++)
                    repository.SaveStickerLayout(new(seeds[index].Id, screen.DeviceName,
                        80 + index % 2 * (size.Width + 100), 150 + index / 2 * 130, size.Width, size.Height));
                fixture = new(seeds.ToArray());
                File.WriteAllText(fixturePath, JsonSerializer.Serialize(fixture, Json));
                (UserSettings.Default with
                {
                    CaptureHotkey = new Hotkey(7, (int)Keys.F15), RecentHotkey = new Hotkey(7, (int)Keys.F16),
                    IntroShown = true, DisplayMode = BookmarkDisplayMode.Stickers,
                    StickerPresentationVersion = UserSettings.CurrentStickerPresentationVersion
                }).Save(data);
            }

            using var worker = new WorkerClient(workerPath);
            using var panelFont = new Font("맑은 고딕", 9F);
            using var panel = new Form
            {
                Text = "BookMark 트리·색상 실기 검증", StartPosition = FormStartPosition.Manual,
                ClientSize = new Size(590, 580), ShowInTaskbar = true, FormBorderStyle = FormBorderStyle.Sizable,
                Font = panelFont, MinimumSize = new Size(530, 500)
            };
            var workArea = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
            panel.Location = new Point(Math.Max(workArea.Left, workArea.Right - panel.Width - 25), workArea.Top + 35);
            var instructions = new Label
            {
                Text = "실제 제품 창에서 B를 A 위로, C를 B 위로 끌어 놓으세요.\n우클릭 메뉴에서 색상과 ‘최상위로 꺼내기’를 확인하세요.\n이 제어 창은 저장소 관찰·창 표시·실패 주입만 제공합니다.",
                Bounds = new Rectangle(16, 14, 555, 68), AutoSize = false
            };
            var showList = Button("실제 목록 열기", 16, 92, 174);
            var showStickers = Button("스티커 표시 / 다시 읽기", 198, 92, 220);
            var hideStickers = Button("스티커 숨기기", 426, 92, 145);
            var restart = Button("제품 보기 재시작 · DB 재읽기", 16, 135, 250);
            var snapshot = Button("증거 스냅샷 기록", 274, 135, 168);
            var exit = Button("정상 종료", 450, 135, 121);
            var failParent = new CheckBox { Text = "관계 저장 실패 주입", AutoSize = true, Location = new Point(16, 186) };
            var failColor = new CheckBox { Text = "색상 저장 실패 주입", AutoSize = true, Location = new Point(265, 186) };
            failParent.CheckedChanged += (_, _) => Volatile.Write(ref rejectParents, failParent.Checked ? 1 : 0);
            failColor.CheckedChanged += (_, _) => Volatile.Write(ref rejectColors, failColor.Checked ? 1 : 0);
            var summary = new TextBox
            {
                ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true,
                Bounds = new Rectangle(16, 220, 555, 228), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                AccessibleName = "실제 저장소 관계와 색상 상태"
            };
            var limitation = new Label
            {
                Text = "입력 도구의 창 목록에 실제 목록/스티커가 나타나는지 확인하세요.\n이 제어 창이 보인다는 사실만으로 제품 창을 조작했다고 판정하지 않습니다.\n제품 창 스타일·소유 관계를 변경하지 않습니다.",
                Bounds = new Rectangle(16, 460, 555, 65), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            var status = new Label { Text = "준비 중", Bounds = new Rectangle(16, 538, 555, 25), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            panel.Controls.AddRange([instructions, showList, showStickers, hideStickers, restart, snapshot, exit, failParent, failColor, summary, limitation, status]);
            BookmarkApplicationContext? context = null;
            int session = 0;
            string? previousState = null;
            BookmarkApplicationContext NewContext()
            {
                session++;
                var application = new BookmarkApplicationContext(repository, worker, data);
                application.ThreadExit += (_, _) => { if (!panel.IsDisposed) panel.Close(); };
                return application;
            }
            object ReadState(string stage)
            {
                foreach (var seed in fixture.Items)
                    if (Hash(seed.Path) != seed.Sha256) throw new InvalidDataException("Synthetic source was modified.");
                // Use the same repository lock as the live product for one consistent
                // observation; no UI handlers or persistence methods run here.
                object gate = context is null ? repository : Field<object>(context, "_repositoryLock");
                lock (gate)
                {
                    var bookmarks = fixture.Items.Select(seed => repository.Get(seed.Id)).ToArray();
                    var layouts = repository.GetStickerLayouts();
                    var windows = Application.OpenForms.Cast<Form>().Select(form => new
                    {
                        type = form.GetType().Name, form.Text, form.Visible, form.Bounds, form.DeviceDpi,
                        form.ShowInTaskbar, borderStyle = form.FormBorderStyle.ToString(), form.TopMost,
                        handle = form.IsHandleCreated ? form.Handle.ToInt64() : 0,
                        bookmark = form.GetType().Name == "StickerForm" ? Property<Bookmark>(form, "Bookmark") : null,
                        editingNote = form.GetType().Name == "StickerForm" && Property<bool>(form, "IsEditingNote"),
                        background = form.BackColor.ToArgb()
                    }).ToArray();
                    var rows = BookmarkTree.Flatten(bookmarks.OfType<Bookmark>()).Select(row => new
                    {
                        row.Bookmark.Id, row.Bookmark.Note, row.Bookmark.ParentId, row.Bookmark.Color, row.Depth, row.HasChildren
                    }).ToArray();
                    string state = JsonSerializer.Serialize(new { bookmarks, layouts, windows, attempts = attempts.ToArray() }, CompactJson);
                    if (state != previousState)
                    {
                        previousState = state;
                        File.AppendAllText(Path.Combine(root, "state-changes.jsonl"), JsonSerializer.Serialize(new
                        {
                            atUtc = DateTimeOffset.UtcNow, session, stage, bookmarks, layouts, windows, attempts = attempts.ToArray()
                        }, CompactJson) + Environment.NewLine);
                    }
                    string Describe(BookmarkTreeRow row)
                    {
                        string parent = row.Bookmark.ParentId is Guid id ? bookmarks.FirstOrDefault(item => item?.Id == id)?.Note ?? id.ToString() : "최상위";
                        return new string(' ', row.Depth * 3) + row.Bookmark.Note + "\r\n" + new string(' ', row.Depth * 3) +
                            $"  상위: {parent}  |  색상: {row.Bookmark.Color}";
                    }
                    string summaryText = "실제 DB 상태 — 제품 보기 세션 " + session + "\r\n\r\n" +
                        string.Join("\r\n\r\n", BookmarkTree.Flatten(bookmarks.OfType<Bookmark>()).Select(Describe)) +
                        "\r\n\r\n저장 시도: " + attempts.Count + " (실패 주입: " + attempts.Count(value => value.Rejected) + ")";
                    if (!summary.IsDisposed && summary.Text != summaryText) summary.Text = summaryText;
                    return new
                    {
                        stage, atUtc = DateTimeOffset.UtcNow, pid = Environment.ProcessId, session,
                        productAssemblySha256 = productHash, workerExecutable = workerPath,
                        sourceUnchanged = true, failureParentInjected = Volatile.Read(ref rejectParents) != 0,
                        failureColorInjected = Volatile.Read(ref rejectColors) != 0,
                        bookmarks, rows, layouts, windows, attempts = attempts.ToArray(),
                        acceptanceScope = "Actual product forms and SQLite; host only shows/restarts product views and observes. Native input must target a window returned by the automation provider. Installed Program.Main remains a separate gate."
                    };
                }
            }
            void Observe(string stage, bool snapshotFile = false)
            {
                string json = JsonSerializer.Serialize(ReadState(stage), Json);
                string temporary = Path.Combine(root, "latest-state.tmp");
                File.WriteAllText(temporary, json);
                File.Move(temporary, Path.Combine(root, "latest-state.json"), true);
                if (snapshotFile)
                    File.WriteAllText(Path.Combine(root, "snapshot-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + stage + ".json"), json);
                if (!status.IsDisposed) status.Text = "관찰 기록: " + DateTime.Now.ToString("HH:mm:ss") + " · " + stage;
            }
            context = NewContext();
            showList.Click += (_, _) => context?.ShowRecent();
            showStickers.Click += async (_, _) =>
            {
                if (context is null) return;
                object manager = Field<object>(context, "_stickers");
                await (Task)manager.GetType().GetMethod("ShowAllAsync", Private)!.Invoke(manager, [false])!;
            };
            hideStickers.Click += (_, _) =>
            {
                if (context is null) return;
                object manager = Field<object>(context, "_stickers");
                manager.GetType().GetMethod("HideAll", Private)!.Invoke(manager, null);
            };
            restart.Click += (_, _) =>
            {
                if (context is null) return;
                if (Field<HashSet<Guid>>(context, "_mutatingBookmarks").Count > 0 || Application.OpenForms.Cast<Form>()
                    .Any(form => form.GetType().Name == "StickerForm" && Property<bool>(form, "IsEditingNote")))
                {
                    status.Text = "메모 편집과 저장을 마친 뒤 제품 보기를 다시 시작하세요.";
                    return;
                }
                Observe("before-restart", true);
                context.Dispose();
                context = NewContext();
                Observe("after-restart", true);
            };
            snapshot.Click += (_, _) => Observe("operator", true);
            exit.Click += (_, _) => panel.Close();
            using var timer = new System.Windows.Forms.Timer { Interval = 500 };
            timer.Tick += (_, _) =>
            {
                try { Observe("observed"); }
                catch (Exception error) { status.Text = "관찰 실패: " + error.Message; }
            };
            panel.Shown += (_, _) => { Observe("start", true); timer.Start(); };
            try { Application.Run(panel); }
            finally
            {
                timer.Stop();
                context.Dispose();
                context = null;
                Observe("exit", true);
            }
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static Button Button(string text, int left, int top, int width) => new()
    {
        Text = text, Bounds = new Rectangle(left, top, width, 35), AccessibleName = text
    };
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static T Field<T>(object value, string field) => (T)value.GetType().GetField(field, Private)!.GetValue(value)!;
    private static T Property<T>(object value, string property) => (T)value.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(value)!;
    private sealed record Seed(Guid Id, string Label, string Path, string Sha256);
    private sealed record Fixture(Seed[] Items);
    private sealed record CommitAttempt(DateTimeOffset AtUtc, string Operation, bool Rejected);
}
