using System.Reflection;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using WorkBookmark.App;
using WorkBookmark.Core;
using WorkBookmark.Storage;
using System.Text.Json;

namespace WorkBookmark.Desktop.Tests;

internal static class Program
{
    private static readonly List<object> Checks = [];
    static void Assert(bool condition, string name) { Checks.Add(new { name, passed = condition }); if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    [STAThread] static void Main(string[] args)
    {
        // Keep a real message loop alive across fixtures. DoEvents alone uninstalls
        // the WinForms synchronization context when the final fixture form closes.
        if (args.Length > 0) { RunSafely(args); return; }
        using var dispatcher = new Control();
        _ = dispatcher.Handle;
        dispatcher.BeginInvoke((Action)(() =>
        {
            try { RunSafely(args); }
            finally { Application.ExitThread(); }
        }));
        Application.Run();
    }
    private static void RunSafely(string[] args)
    {
        try { Run(args); }
        catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    }
    private static void Run(string[] args)
    {
        if (args.Length == 3 && args[0] == "--sticker-worker" && args[2] == "--worker") { StickerOperationChecks.RunWorker(args[1]); return; }
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 1 && args[0] == "--input-monitor-only") { InputMonitorChecks.Run(Assert); return; }
        if (args.Length == 1 && args[0] == "--stickers-only") { StickerFormChecks.Run(Assert); return; }
        if (args.Length == 2 && args[0] == "--browser-sqlite") { Environment.ExitCode = BrowserSqliteChecks.Run(args[1]); return; }
        if (args.Length == 2 && args[0] == "--render-branding") { BrandingRenderChecks.Run(args[1]); return; }
        if (args.Length == 2 && args[0] == "--render-stickers") { StickerFormChecks.Render(args[1]); StickerInlineNoteChecks.Render(args[1]); return; }
        string data = Path.Combine(Path.GetTempPath(), "WorkBookmark-UiQa-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(data);
        SettingsDisplayChecks.Run(Path.Combine(data, "display-settings"), Assert);
        StickerFormChecks.Run(Assert);
        StickerInlineNoteChecks.Run(Assert);
        BookmarkTypeIconChecks.Run(Assert);
        StickerStartupChecks.Run(Path.Combine(data, "sticker-startup"), Assert);
        StickerOperationChecks.Run(Path.Combine(data, "sticker-operations"), Assert);
        StickerIntegrationChecks.Run(Path.Combine(data, "sticker-integration"), Assert);
        BookmarkRefreshChecks.Run(Path.Combine(data, "bookmark-refresh"), Assert);
        StickerPersistenceChecks.Run(Assert);
        ResumeNotificationChecks.Run(Path.Combine(data, "resume-notifications"), Assert);
        var custom = UserSettings.Default with { CaptureHotkey = new Hotkey(7, (int)Keys.F19), RecentHotkey = new Hotkey(7, (int)Keys.F20), IntroShown = true };
        UserSettings.Default.Save(data); custom.Save(data);
        Assert(UserSettings.Load(data) == custom, "settings atomic overwrite round-trip");
        Assert(Directory.GetFiles(data, "*.tmp").Length == 0, "settings no temporary leftovers");
        File.WriteAllText(Path.Combine(data, "settings.json"), "{invalid"); bool refused = false; try { UserSettings.Load(data); } catch { refused = true; }
        Assert(refused && File.ReadAllText(Path.Combine(data, "settings.json")) == "{invalid", "corrupt settings preserved");
        using var first = new HotkeyWindow(new Hotkey(7, (int)Keys.F19), new Hotkey(7, (int)Keys.F20));
        Assert(first.CaptureRegistered && first.RecentRegistered, "native hotkeys registered");
        using var blocker = new HotkeyWindow(new Hotkey(7, (int)Keys.F21), new Hotkey(7, (int)Keys.F22));
        Assert(blocker.CaptureRegistered && blocker.RecentRegistered, "conflict fixture registered");
        Assert(!first.TryUpdate(new Hotkey(7, (int)Keys.F23), new Hotkey(7, (int)Keys.F21), out _), "replacement collision refused");
        using var check = new HotkeyWindow(new Hotkey(7, (int)Keys.F19), new Hotkey(7, (int)Keys.F23));
        Assert(!check.CaptureRegistered && check.RecentRegistered, "old registration retained and pending reservation rolled back");
        Assert(!first.TryUpdate(new Hotkey(7, (int)Keys.F24), new Hotkey(7, (int)Keys.F24), out _), "duplicate shortcut pair refused");
        string target = Path.Combine(data, "한글 공백 (시험)", "WorkBookmark.exe"); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, [0]);
        byte[] bytes = (byte[])typeof(StartupRegistration).GetMethod("CreateShortcut", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [target])!;
        string shortcut = Path.Combine(data, "shortcut.lnk"); File.WriteAllBytes(shortcut, bytes);
        object shell = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!)!;
        try
        {
            ((IPersistFile)shell).Load(shortcut, 0);
            var path = new StringBuilder(32768); ((IShellLinkW)shell).GetPath(path, path.Capacity, IntPtr.Zero, 4);
            Assert(path.ToString() == target, "Windows Shell parses Unicode startup target");
            var description = new StringBuilder(1000); ((IShellLinkW)shell).GetDescription(description, description.Capacity);
            Assert(description.ToString() == "WorkBookmark per-user startup shortcut v1", "startup ownership description parsed");
            var working = new StringBuilder(32768); ((IShellLinkW)shell).GetWorkingDirectory(working, working.Capacity);
            Assert(working.ToString() == Path.GetDirectoryName(target), "startup working directory parsed");
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        var assembly = typeof(UserSettings).Assembly;
        var imeType = assembly.GetType("WorkBookmark.App.UI.ImeTextBox")!;
        using (var ime = (Control)Activator.CreateInstance(imeType)!)
        {
            SendMessage(ime.Handle, 0x010D, IntPtr.Zero, IntPtr.Zero);
            Assert((bool)imeType.GetProperty("IgnoreSubmit")!.GetValue(ime)!, "IME composition suppresses submission");
            SendMessage(ime.Handle, 0x010E, IntPtr.Zero, IntPtr.Zero);
            Assert((bool)imeType.GetProperty("IgnoreSubmit")!.GetValue(ime)!, "IME commit Enter has a suppression window");
        }
        var sample = new Bookmark(Guid.NewGuid(), new CapturedTarget(TargetKind.File, target), target, "WorkBookmark.exe", "진행 메모", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, null, null, null, null);
        bool failNote = true;
        string? saved = null;
        Func<string, Task> persist = value => { if (failNote) return Task.FromException(new IOException("Injected storage failure")); saved = value; return Task.CompletedTask; };
        Type noteType = assembly.GetType("WorkBookmark.App.UI.NoteForm")!;
        using (var note = (Form)Activator.CreateInstance(noteType, sample, persist)!)
        {
            var editor = (TextBox)noteType.GetField("_note", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(note)!;
            editor.Text = "실패 후 보존할 메모";
            var save = noteType.GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((Task)save.Invoke(note, null)!).GetAwaiter().GetResult();
            Assert(!note.IsDisposed && editor.Text == "실패 후 보존할 메모" && !editor.ReadOnly, "note write failure retains editable content");
            failNote = false;
            ((Task)save.Invoke(note, null)!).GetAwaiter().GetResult();
            Assert(saved == "실패 후 보존할 메모", "note retry saves retained text");
        }
        Type recentType = assembly.GetType("WorkBookmark.App.UI.RecentForm")!;
        Func<string, Task<SearchResults>> load = _ => Task.FromResult(new SearchResults([sample], false));
        using (var recent = (Form)Activator.CreateInstance(recentType, load)!)
        {
            Task open = (Task)recentType.GetMethod("OpenAsync")!.Invoke(recent, null)!;
            while (!open.IsCompleted) { Application.DoEvents(); Thread.Sleep(10); }
            open.GetAwaiter().GetResult();
            Application.DoEvents();
            Assert(recent.Visible && ((Bookmark?)recentType.GetProperty("Selected")!.GetValue(recent))?.Id == sample.Id, "recent form opens with loaded first selection");
            if (args.Length == 1)
            {
                using var bitmap = new Bitmap(recent.Width, recent.Height);
                recent.DrawToBitmap(bitmap, new Rectangle(Point.Empty, recent.Size));
                bitmap.Save(Path.Combine(Path.GetDirectoryName(args[0])!, "recent-component.png"), ImageFormat.Png);
            }
        }
        InputMonitorChecks.Run(Assert);
        CaptureInputChecks(data, target);
        NotepadSnapshotChecks(data);
        RecentCountChecks(assembly, sample);
        AdditionalFormChecks.Run(Assert);
        BrowserCommitChecks(data);
        Console.WriteLine($"RESULT: {Checks.Count} checks passed. Native launch, actual IME and rendered UI remain separate manual gates.");
        if (args.Length == 1) File.WriteAllText(args[0], JsonSerializer.Serialize(new { suite = "Desktop checks", checks = Checks, limitations = "Synthetic IME messages test event suppression only; actual Korean IME and rendered interaction require manual verification." }, new JsonSerializerOptions { WriteIndented = true }));

    }
    private static void CaptureInputChecks(string data, string target)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var constructor = typeof(BookmarkApplicationContext).GetConstructor(instance, null,
            [typeof(IBookmarkRepository), typeof(WorkerClient), typeof(string), typeof(Func<WorkerRequest, CancellationToken, Task<WorkerResponse>>)], null)!;
        var capture = typeof(BookmarkApplicationContext).GetMethod("CaptureAsync", instance)!;
        var monitorField = typeof(BookmarkApplicationContext).GetField("_captureInput", instance)!;
        string settingsPath = Path.Combine(data, "capture-input");
        Directory.CreateDirectory(settingsPath);
        (UserSettings.Default with { CaptureHotkey = new Hotkey(7, (int)Keys.F15), RecentHotkey = new Hotkey(7, (int)Keys.F16), IntroShown = true }).Save(settingsPath);
        var observedTarget = new CapturedTarget(TargetKind.File, target);

        void Inject(object monitor, string method, int message)
        {
            object observer = monitor.GetType().GetField("_observer", instance)!.GetValue(monitor)!;
            observer.GetType().GetMethod(method, instance)!.Invoke(observer, [0, (IntPtr)message, IntPtr.Zero]);
        }
        void PumpUntil(Func<bool> finished)
        {
            var limit = DateTime.UtcNow.AddSeconds(5);
            while (!finished() && DateTime.UtcNow < limit) { Application.DoEvents(); Thread.Sleep(1); }
            if (!finished()) throw new TimeoutException("Capture UI test did not finish.");
        }
        void Scenario(string name, Action<object, TaskCompletionSource<WorkerResponse>, WorkerRequest> beforeResponse, bool expectedSave, bool blockCommit = false)
        {
            using var repository = new CaptureRepository(observedTarget, blockCommit);
            using var worker = new WorkerClient();
            var response = new TaskCompletionSource<WorkerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            WorkerRequest? request = null;
            CancellationToken token = default;
            Func<WorkerRequest, CancellationToken, Task<WorkerResponse>> observe = (value, cancellation) => { request = value; token = cancellation; return response.Task; };
            using var context = (BookmarkApplicationContext)constructor.Invoke([repository, worker, settingsPath, observe]);
            try
            {
                var pending = (Task)capture.Invoke(context, null)!;
                object monitor = monitorField.GetValue(context) ?? throw new Exception("Input monitoring did not precede worker observation.");
                if (request is null) throw new Exception("Capture worker did not start.");
                beforeResponse(monitor, response, request);
                response.TrySetResult(new(1, request.RequestId, ResultCode.Captured, observedTarget));
                if (blockCommit)
                {
                    PumpUntil(() => repository.WriteStarted.IsSet);
                    // A delayed callback after observation acceptance must not cancel the DB write.
                    Inject(monitor, "Keyboard", 0x0100);
                    if (token.IsCancellationRequested) throw new Exception("Accepted capture was cancelled during persistence.");
                    repository.AllowWrite.Set();
                }
                PumpUntil(() => pending.IsCompleted);
                pending.GetAwaiter().GetResult();
                Assert(repository.CaptureCount == (expectedSave ? 1 : 0) && token.IsCancellationRequested == !expectedSave, name);
                // The already released monitor must remain harmless after the request is complete.
                Inject(monitor, "Mouse", 0x0201);
            }
            finally { repository.AllowWrite.Set(); context.ExitThread(); }
        }

        Scenario("capture shortcut key-up preserves the observed target", (monitor, _, _) =>
        {
            Inject(monitor, "Keyboard", 0x0101); Inject(monitor, "Keyboard", 0x0105);
            Inject(monitor, "Mouse", 0x0200); Inject(monitor, "Mouse", 0x0202);
        }, true);
        Scenario("new key before worker response prevents capture commit", (monitor, _, _) => Inject(monitor, "Keyboard", 0x0100), false);
        Scenario("new mouse button before worker response prevents capture commit", (monitor, _, _) => Inject(monitor, "Mouse", 0x0201), false);
        Scenario("wheel input before worker response prevents capture commit", (monitor, _, _) => Inject(monitor, "Mouse", 0x020A), false);
        Scenario("new input wins against an already returned capture response", (monitor, response, request) =>
        {
            response.SetResult(new(1, request.RequestId, ResultCode.Captured, observedTarget));
            Inject(monitor, "Keyboard", 0x0104);
        }, false);
        Scenario("accepted capture survives new work during the database write", (_, _, _) => { }, true, blockCommit: true);
    }

    private static void NotepadSnapshotChecks(string data)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(BookmarkApplicationContext);
        var constructor = type.GetConstructor(flags, null,
            [typeof(IBookmarkRepository), typeof(WorkerClient), typeof(string), typeof(Func<WorkerRequest, CancellationToken, Task<WorkerResponse>>)], null)!;
        var capture = type.GetMethod("CaptureAsync", flags)!;
        string settingsPath = Path.Combine(data, "notepad-snapshot"); Directory.CreateDirectory(settingsPath);
        (UserSettings.Default with { CaptureHotkey = new Hotkey(7, (int)Keys.F15), RecentHotkey = new Hotkey(7, (int)Keys.F16), IntroShown = true }).Save(settingsPath);
        void Pump(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(8);
            while (!done() && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(1); }
            if (!done()) throw new TimeoutException("Notepad snapshot flow timed out.");
        }
        string Toast(BookmarkApplicationContext context) => type.GetField("_toast", flags)!.GetValue(context) is Form toast
            ? string.Join(" ", toast.Controls.Cast<Control>().Select(control => control.Text)) : "";
        void Scenario(string name, string body, bool failCommit, bool corruptPayload)
        {
            string database = Path.Combine(settingsPath, Guid.NewGuid().ToString("N") + ".db");
            var expected = NotepadSnapshotPolicy.Create("제목 없음 - 메모장", body, 0, body.Length, true);
            using var started = new ManualResetEventSlim();
            using var allow = new ManualResetEventSlim(false);
            using var repository = new SqliteBookmarkRepository(database, operation =>
            {
                if (operation != "capture") return;
                started.Set();
                if (!allow.Wait(TimeSpan.FromSeconds(6))) throw new TimeoutException();
                if (failCommit) throw new IOException("Injected snapshot commit failure");
            });
            using var worker = new WorkerClient();
            int requests = 0;
            Func<WorkerRequest, CancellationToken, Task<WorkerResponse>> observe = async (request, token) =>
            {
                requests++;
                var target = corruptPayload ? expected with { TextContent = body + "changed" } : expected;
                using var wire = new MemoryStream();
                await FrameProtocol.WriteAsync(wire, new WorkerResponse(1, request.RequestId, ResultCode.Captured, target), token);
                wire.Position = 0;
                return await FrameProtocol.ReadAsync<WorkerResponse>(wire, token);
            };
            using var context = (BookmarkApplicationContext)constructor.Invoke([repository, worker, settingsPath, observe]);
            try
            {
                var pending = (Task)capture.Invoke(context, null)!;
                Pump(() => started.IsSet || pending.IsCompleted);
                if (!corruptPayload)
                    Assert(started.IsSet && !pending.IsCompleted && !Toast(context).Contains("보관했습니다", StringComparison.Ordinal), name + ": success waits for durable SQLite commit");
                allow.Set(); Pump(() => pending.IsCompleted); pending.GetAwaiter().GetResult();
                var result = repository.List();
                bool committed = !failCommit && !corruptPayload;
                Assert(requests == 1 && result.Items.Count == (committed ? 1 : 0), name + ": single capture without file selection");
                if (committed)
                {
                    var stored = result.Items.Single();
                    Assert(stored.Target == expected && Toast(context).Contains("보관했습니다", StringComparison.Ordinal), name + ": original body and selection committed before success");
                    using var reopened = new SqliteBookmarkRepository(database);
                    Assert(reopened.Get(stored.Id)?.Target == expected, name + ": snapshot survives database reopen");
                }
                else
                    Assert(!Toast(context).Contains("보관했습니다", StringComparison.Ordinal) && result.Items.Count == 0, name + ": rejected or rolled-back snapshot cannot report success");
            }
            finally { allow.Set(); context.ExitThread(); }
        }
        Scenario("unsaved Korean and emoji snapshot", "아직 저장하지 않은 메모 😀\n둘째 줄", false, false);
        Scenario("empty new Notepad snapshot", "", false, false);
        Scenario("snapshot SQLite write failure", "실패 주입 합성 본문", true, false);
        Scenario("snapshot content identity mismatch", "무결성 합성 본문", false, true);
    }

    private static void RecentCountChecks(Assembly assembly, Bookmark sample)
    {
        Type recentType = assembly.GetType("WorkBookmark.App.UI.RecentForm")!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Func<string, Task<SearchResults>> load = query => Task.FromResult(new SearchResults(
            Enumerable.Range(0, string.IsNullOrWhiteSpace(query) ? 20 : 100).Select(_ => sample with { Id = Guid.NewGuid() }).ToArray(), true));
        using var recent = (Form)Activator.CreateInstance(recentType, load)!;
        var task = (Task)recentType.GetMethod("OpenAsync")!.Invoke(recent, null)!;
        while (!task.IsCompleted) { Application.DoEvents(); Thread.Sleep(1); }
        task.GetAwaiter().GetResult();
        var label = (Label)recentType.GetField("_status", flags)!.GetValue(recent)!;
        Assert(label.Text.Contains("최근 20개", StringComparison.Ordinal) && !label.Text.Contains("100개", StringComparison.Ordinal), "recent list over twenty records uses recent-count guidance");
        var search = (TextBox)recentType.GetField("_search", flags)!.GetValue(recent)!;
        search.Text = "fixture";
        task = (Task)recentType.GetMethod("ReloadAsync")!.Invoke(recent, [true])!;
        while (!task.IsCompleted) { Application.DoEvents(); Thread.Sleep(1); }
        task.GetAwaiter().GetResult();
        Assert(label.Text.Contains("검색 결과 100개", StringComparison.Ordinal), "search limit remains one hundred results");
    }

    private static void BrowserCommitChecks(string data)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var capture = typeof(BookmarkApplicationContext).GetMethod("CaptureBrowserAsync", flags)!;
        string settingsPath = Path.Combine(data, "browser-commit"); Directory.CreateDirectory(settingsPath);
        (UserSettings.Default with { CaptureHotkey = new Hotkey(7, (int)Keys.F15), RecentHotkey = new Hotkey(7, (int)Keys.F16), IntroShown = true }).Save(settingsPath);
        var target = new CapturedTarget(TargetKind.WebPage, "https://example.test/document?q=one#two", PageTitle: "검증 페이지");
        void Pump(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!done() && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(1); }
            if (!done()) throw new TimeoutException("Browser commit test timed out.");
        }
        using var repository = new CaptureRepository(target, true);
        using var worker = new WorkerClient();
        using var context = new BookmarkApplicationContext(repository, worker, settingsPath);
        Task<BrowserCaptureResponse> Submit(BrowserCaptureRequest request, CancellationToken token = default) =>
            (Task<BrowserCaptureResponse>)capture.Invoke(context, [request, token])!;
        var request = new BrowserCaptureRequest(1, "capture", Guid.NewGuid(), target.Path, target.PageTitle!);
        try
        {
            var pending = Submit(request); Pump(() => repository.WriteStarted.IsSet);
            Assert(!pending.IsCompleted && repository.CaptureCount == 0, "browser success waits for repository commit");
            var busy = Submit(request with { RequestId = Guid.NewGuid() }); Pump(() => busy.IsCompleted);
            Assert(!busy.Result.Success && busy.Result.Code == "AppBusy", "browser simultaneous request returns busy without another write");
            repository.AllowWrite.Set(); Pump(() => pending.IsCompleted);
            Assert(pending.Result.Success && pending.Result.RequestId == request.RequestId && pending.Result.Code == "CaptureCommitted" && repository.CaptureCount == 1, "browser exact acknowledgment follows committed capture");
            repository.FailWrite = true;
            var failed = Submit(request with { RequestId = Guid.NewGuid() }); Pump(() => failed.IsCompleted);
            Assert(!failed.Result.Success && failed.Result.Code == "PersistenceFailed" && repository.CaptureCount == 1, "browser repository failure cannot report successful capture");
            repository.FailWrite = false;
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var stopped = Submit(request with { RequestId = Guid.NewGuid() }, cancelled.Token); Pump(() => stopped.IsCompleted); Application.DoEvents();
            Assert(!stopped.IsCompletedSuccessfully && repository.CaptureCount == 1, "browser cancelled queued request cannot write");
            var invalid = Submit(request with { RequestId = Guid.NewGuid(), Url = "file:///C:/data.txt" }); Pump(() => invalid.IsCompleted);
            Assert(!invalid.Result.Success && repository.CaptureCount == 1, "browser invalid URI cannot reach repository");
        }
        finally { repository.AllowWrite.Set(); context.ExitThread(); }
    }

    private sealed class CaptureRepository(CapturedTarget target, bool blockWrite) : IBookmarkRepository
    {
        internal int CaptureCount;
        internal bool FailWrite;
        internal readonly ManualResetEventSlim WriteStarted = new();
        internal readonly ManualResetEventSlim AllowWrite = new(!blockWrite);
        public CaptureCommit UpsertCapture(CapturedTarget value)
        {
            WriteStarted.Set();
            if (!AllowWrite.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Storage test gate timed out.");
            if (FailWrite) throw new IOException("Injected write failure");
            if (value != target) throw new Exception("Capture changed before persistence.");
            Interlocked.Increment(ref CaptureCount);
            var now = DateTimeOffset.UtcNow;
            return new(new(Guid.NewGuid(), value, value.Path, Path.GetFileName(value.Path), "", now, now, 1, null, null, null, null), false, false);
        }
        public SearchResults List(string query = "") => new([], false);
        public Bookmark? Get(Guid id) => null;
        public void UpdateNote(Guid id, string note) => throw new NotSupportedException();
        public void SoftDelete(Guid id) => throw new NotSupportedException();
        public void Restore(Guid id) => throw new NotSupportedException();
        public void RecordResume(Guid id, ResultCode result) => throw new NotSupportedException();
        public void Relink(Guid id, CapturedTarget validatedTarget) => throw new NotSupportedException();
        public void Dispose() { WriteStarted.Dispose(); AllowWrite.Dispose(); }
    }

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr findData, uint flags);
        void GetIDList(out IntPtr list); void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder working, int count);
    }
}
