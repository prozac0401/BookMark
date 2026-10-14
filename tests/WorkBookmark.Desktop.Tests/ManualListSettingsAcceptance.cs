using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using WorkBookmark.App;
using WorkBookmark.Core;
using WorkBookmark.Storage;

namespace WorkBookmark.Desktop.Tests;

/// <summary>Real product windows and SQLite, with native input supplied by the operator.</summary>
internal static class ManualListSettingsAcceptance
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string MarkerName = ".owned-manual-list-settings";
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    internal static void Run(string directory, string workerExecutable)
    {
        ApplicationConfiguration.Initialize();
        string root = Path.GetFullPath(directory);
        if (!IsAllowedRoot(root)) throw new InvalidOperationException("Manual fixture must be under this checkout's artifacts/manual-ui directory.");
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
            string marker = Path.Combine(root, MarkerName);
            if (Directory.Exists(root) && (!File.Exists(marker) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0))
                throw new InvalidOperationException("Existing fixture directory is not owned by this harness.");
            Directory.CreateDirectory(root);
            File.WriteAllText(marker, "WorkBookmark isolated list and settings acceptance v1");
            string data = Path.Combine(root, "data");
            Directory.CreateDirectory(data);
            DiagnosticLog.Initialize(data);
            bool originalStartup = StartupRegistration.IsEnabled;
            string? originalStartupHash = File.Exists(StartupRegistration.ShortcutPath) ? Hash(StartupRegistration.ShortcutPath) : null;
            using var repository = new SqliteBookmarkRepository(Path.Combine(data, "bookmarks.db"));
            string fixturePath = Path.Combine(root, "fixture.json");
            Fixture fixture;
            if (File.Exists(fixturePath))
                fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(fixturePath), Json) ?? throw new InvalidDataException("Invalid fixture metadata.");
            else
            {
                var folder = repository.CreateFolder("검증 폴더");
                var nested = repository.CreateFolder("하위 폴더", folder.Id);
                string ordinaryPath = Path.Combine(root, "편집 가능한 합성 책갈피.txt");
                string deepPath = Path.Combine(root, "nested-search-target.txt");
                File.WriteAllText(ordinaryPath, "Synthetic source for list and settings acceptance. Do not modify.");
                File.WriteAllText(deepPath, "Nested synthetic source for search clearing acceptance. Do not modify.");
                var ordinary = repository.UpsertCapture(new CapturedTarget(TargetKind.File, ordinaryPath)).Bookmark;
                var deep = repository.UpsertCapture(new CapturedTarget(TargetKind.File, deepPath)).Bookmark;
                repository.UpdateNote(ordinary.Id, "편집할 메모");
                repository.UpdateNote(deep.Id, "검색 확인 대상");
                repository.SetParent(ordinary.Id, folder.Id);
                repository.SetParent(deep.Id, nested.Id);
                repository.SetFolderExpanded(folder.Id, false);
                repository.SetFolderExpanded(nested.Id, false);
                var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
                var managerType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.StickerManager")!;
                var size = (Size)managerType.GetProperty("DefaultExpandedSize96", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                var ordered = new[] { folder, nested, ordinary, deep };
                for (int index = 0; index < ordered.Length; index++)
                    repository.SaveStickerLayout(new(ordered[index].Id, screen.DeviceName,
                        80, 150 + index * 90, size.Width, size.Height, ParentId: repository.Get(ordered[index].Id)!.ParentId));
                fixture = new(folder.Id, nested.Id, ordinary.Id, deep.Id,
                    [new(ordinaryPath, Hash(ordinaryPath)), new(deepPath, Hash(deepPath))]);
                File.WriteAllText(fixturePath, JsonSerializer.Serialize(fixture, Json));
                (UserSettings.Default with
                {
                    CaptureHotkey = new Hotkey(7, (int)Keys.F15), RecentHotkey = new Hotkey(7, (int)Keys.F16),
                    StartWithWindows = originalStartup, IntroShown = true, DisplayMode = BookmarkDisplayMode.Stickers,
                    StickerPresentationVersion = UserSettings.CurrentStickerPresentationVersion
                }).Save(data);
            }

            using var worker = new WorkerClient(workerPath);
            using var panelFont = new Font("맑은 고딕", 9F);
            using var panel = new Form
            {
                Text = "BookMark 목록·설정 실기 검증", StartPosition = FormStartPosition.Manual,
                ClientSize = new Size(590, 460), ShowInTaskbar = true, Font = panelFont,
                MinimumSize = new Size(560, 460)
            };
            var area = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
            panel.Location = new Point(Math.Max(area.Left, area.Right - panel.Width - 25), area.Top + 35);
            var instructions = new Label
            {
                Text = "실제 제품 창에서 폴더 펼침·검색 해제·설정 닫기/Esc/적용을 확인하세요.\n스티커 클릭·메모 편집과 다른 앱 전환도 실제 입력으로 확인하세요.\n시작프로그램 체크는 바꾸지 마세요. 이 항목의 저장은 격리됩니다.",
                Bounds = new Rectangle(16, 14, 555, 75)
            };
            var showList = Button("실제 목록 열기", 16, 96, 170);
            var showSettings = Button("실제 설정 열기", 198, 96, 170);
            var exit = Button("정상 종료", 380, 96, 170);
            var focusLabel = new Label { Text = "포커스 전환용 입력칸 (제품 데이터와 무관)", Bounds = new Rectangle(16, 146, 555, 23) };
            var focusBox = new TextBox { Bounds = new Rectangle(16, 173, 555, 28), AccessibleName = "검증 제어 창 포커스 입력칸" };
            var summary = new TextBox
            {
                ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true,
                Bounds = new Rectangle(16, 215, 555, 188), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                AccessibleName = "실제 목록 설정 저장소 관찰 상태"
            };
            var status = new Label { Text = "준비 중", Bounds = new Rectangle(16, 420, 555, 25), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            panel.Controls.AddRange([instructions, showList, showSettings, exit, focusLabel, focusBox, summary, status]);
            BookmarkApplicationContext? context = new(repository, worker, data);
            context.ThreadExit += (_, _) => { if (!panel.IsDisposed) panel.Close(); };
            Form? isolatedSettings = null;
            int applyCount = 0;
            string? applyError = null;
            string? previousState = null;
            object ReadState(string stage)
            {
                bool sourcesUnchanged = fixture.Sources.All(source => Hash(source.Path) == source.Sha256);
                string? startupHash = File.Exists(StartupRegistration.ShortcutPath) ? Hash(StartupRegistration.ShortcutPath) : null;
                object gate = context is null ? repository : Field<object>(context, "_repositoryLock");
                lock (gate)
                {
                    var bookmarks = repository.ListActive();
                    var forms = Application.OpenForms.Cast<Form>().ToArray();
                    var windows = forms.Select(form => new
                    {
                        type = form.GetType().Name, form.Text, form.Visible, form.Enabled, form.TopMost, form.Bounds, form.DeviceDpi,
                        form.ShowInTaskbar, form.IsDisposed, handle = form.IsHandleCreated ? form.Handle.ToInt64() : 0,
                        bookmark = form.GetType().Name == "StickerForm" ? Property<Bookmark>(form, "Bookmark") : null,
                        editingNote = form.GetType().Name == "StickerForm" && Property<bool>(form, "IsEditingNote"),
                        noteDraft = form.GetType().Name == "StickerForm" ? Field<TextBox>(form, "_noteEditor").Text : null
                    }).ToArray();
                    var recent = context is null ? null : Field<Form>(context, "_recent");
                    var settingsForm = forms.FirstOrDefault(form => form.GetType().Name == "SettingsForm");
                    var recentItemIds = recent is { IsDisposed: false } ? Field<ListBox>(recent, "_items").Items.Cast<Bookmark>().Select(item => item.Id).ToArray() : [];
                    string? search = recent is { IsDisposed: false } ? Field<TextBox>(recent, "_search").Text : null;
                    var persistedSettings = UserSettings.Load(data);
                    var observation = new
                    {
                        foregroundHwnd = GetForegroundWindow().ToInt64(), windows, bookmarks,
                        folderState = bookmarks.Where(bookmark => bookmark.IsFolder).Select(bookmark => new { bookmark.Id, bookmark.ParentId, bookmark.FolderExpanded }).ToArray(),
                        recentItemIds, search, persistedSettings, settingsSha256 = Hash(Path.Combine(data, "settings.json")), applyCount, applyError,
                        settingsOpen = settingsForm is { Visible: true, IsDisposed: false },
                        settingsDraft = settingsForm is null ? null : new
                        {
                            stickerSnapEnabled = Field<CheckBox>(settingsForm, "_stickerSnap").Checked,
                            stickers = Field<RadioButton>(settingsForm, "_stickerDisplay").Checked,
                            startup = Field<CheckBox>(settingsForm, "_startup").Checked
                        },
                        sourcesUnchanged, startupShortcutUnchanged = startupHash == originalStartupHash
                    };
                    string compact = JsonSerializer.Serialize(observation, CompactJson);
                    if (compact != previousState)
                    {
                        previousState = compact;
                        File.AppendAllText(Path.Combine(root, "state-changes.jsonl"), JsonSerializer.Serialize(new { atUtc = DateTimeOffset.UtcNow, stage, observation }, CompactJson) + Environment.NewLine);
                    }
                    string summaryText = $"폴더: {bookmarks.Single(bookmark => bookmark.Id == fixture.FolderId).FolderExpanded} / 하위 폴더: {bookmarks.Single(bookmark => bookmark.Id == fixture.NestedId).FolderExpanded}\r\n" +
                        $"목록 표시: {recent is { Visible: true }} / 행 수: {recentItemIds.Length}\r\n검색: {search}\r\n설정 열림: {settingsForm is { Visible: true }} / 저장된 자석 정렬: {persistedSettings.StickerSnapEnabled}\r\n" +
                        $"전경 HWND: {observation.foregroundHwnd}\r\n원본 합성 파일 보존: {sourcesUnchanged}\r\n시작프로그램 바로가기 보존: {observation.startupShortcutUnchanged}\r\n\r\n증거: latest-state.json 및 state-changes.jsonl";
                    if (!summary.IsDisposed && summary.Text != summaryText) summary.Text = summaryText;
                    return new
                    {
                        stage, atUtc = DateTimeOffset.UtcNow, pid = Environment.ProcessId,
                        productAssemblySha256 = productHash, workerExecutable = workerPath, fixture,
                        startupScope = "Actual product SettingsForm + isolated settings persistence; startup registration is NOT RUN. The settings apply callback only calls UserSettings.Save on the synthetic profile.",
                        acceptanceScope = "Actual product forms and SQLite; recent/stickers use real BookmarkApplicationContext. Host only opens views and observes; every tested action requires native operator input. Installed Program.Main and application-context settings side effects are separate gates.",
                        observation
                    };
                }
            }
            void Observe(string stage)
            {
                string json = JsonSerializer.Serialize(ReadState(stage), Json);
                string temporary = Path.Combine(root, "latest-state.tmp");
                File.WriteAllText(temporary, json);
                File.Move(temporary, Path.Combine(root, "latest-state.json"), true);
                if (!status.IsDisposed) status.Text = "관찰 기록: " + DateTime.Now.ToString("HH:mm:ss") + " · " + stage;
            }
            showList.Click += (_, _) => context?.ShowRecent();
            showSettings.Click += (_, _) =>
            {
                if (isolatedSettings is { IsDisposed: false }) { isolatedSettings.Activate(); return; }
                var settingsType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.UI.SettingsForm")!;
                Func<UserSettings, Task<string?>> apply = async requested =>
                {
                    applyCount++;
                    try { await Task.Run(() => requested.Save(data)); applyError = null; return null; }
                    catch (Exception error) { applyError = error.Message; return "합성 프로필 설정 저장 실패"; }
                };
                isolatedSettings = (Form)Activator.CreateInstance(settingsType,
                    UserSettings.Load(data), true, true, data, apply, (Action)(() => { }), (Action)(() => { }))!;
                isolatedSettings.Show();
            };
            exit.Click += (_, _) => panel.Close();
            using var timer = new System.Windows.Forms.Timer { Interval = 200 };
            timer.Tick += (_, _) =>
            {
                try { Observe("observed"); }
                catch (Exception error) { status.Text = "관찰 실패: " + error.Message; }
            };
            panel.Shown += (_, _) => { Observe("start"); timer.Start(); };
            try { Application.Run(panel); }
            finally
            {
                timer.Stop();
                isolatedSettings?.Dispose();
                context.Dispose();
                context = null;
                Observe("exit");
            }
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static bool IsAllowedRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        string allowed = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "manual-ui"));
        return Path.GetFullPath(root).StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static Button Button(string text, int left, int top, int width) => new() { Text = text, Bounds = new Rectangle(left, top, width, 35), AccessibleName = text };
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static T Field<T>(object value, string field) => (T)value.GetType().GetField(field, Private)!.GetValue(value)!;
    private static T Property<T>(object value, string property) => (T)value.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(value)!;
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    private sealed record Source(string Path, string Sha256);
    private sealed record Fixture(Guid FolderId, Guid NestedId, Guid OrdinaryId, Guid DeepId, Source[] Sources);
}
