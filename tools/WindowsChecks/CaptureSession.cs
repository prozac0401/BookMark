using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using WorkBookmark.App;
using WorkBookmark.Core;
using WorkBookmark.Windows;

// Explicitly started test tool: it never activates windows, sends input, or resumes a document.
internal static class CaptureSession
{
    internal sealed record Plan(int Version, bool SyntheticFixturesOnly, string FixtureRoot, string Gate, Case[] Cases);
    internal sealed record Case(string CaseId, string Scenario, string Instruction, string WindowGroup, string? ViewGroup,
        string? ProcessGroup, CapturedTarget? ExpectedTarget, ResultCode ExpectedResult = ResultCode.Captured);
    internal sealed record Attempt(int Number, Case Case, TargetSnapshot Snapshot, WorkerResponse? Response,
        string Verdict, long ElapsedMilliseconds, DateTimeOffset CapturedAtUtc, bool ActualPathRedacted = false, bool WorkerRequested = false);
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private static readonly HashSet<string> Scenarios = new(StringComparer.Ordinal)
    {
        "explorer-folder", "explorer-file", "explorer-selected-folder", "explorer-same-folder-tabs",
        "explorer-transition", "explorer-rejection", "excel-same-process", "excel-separate-process",
        "excel-stable", "excel-rejection"
    };

    internal static int Run(string[] args)
    {
        if (args.Length != 4) { Console.Error.WriteLine("session APP_EXE PLAN_JSON OUTPUT_JSON"); return 2; }
        try
        {
            string app = Path.GetFullPath(args[1]), input = Path.GetFullPath(args[2]), output = Path.GetFullPath(args[3]);
            if (!File.Exists(app)) throw new ArgumentException("APP_EXE does not exist.");
            if (File.Exists(output)) throw new ArgumentException("Choose a new evidence output file; previous evidence is never overwritten.");
            byte[] bytes = File.ReadAllBytes(input);
            if (bytes.Length > 1_048_576) throw new ArgumentException("Plan exceeds 1 MiB.");
            Plan plan = LoadPlan(bytes, Path.GetDirectoryName(input)!);
            // The plan is immutable for this run. Hash refers to the exact original JSON bytes.
            using var context = new Session(plan, app, output, Convert.ToHexString(SHA256.HashData(bytes)));
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; context.Cancel(); };
            Console.CancelKeyPress += cancel;
            try { context.Start(); if (!context.Finished) Application.Run(context); }
            finally { Console.CancelKeyPress -= cancel; }
            return context.ExitCode;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or BookmarkException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("Session could not start: " + exception.GetType().Name + ". Check the plan, fixture paths, executable, and output permissions.");
            return 2;
        }
    }

    internal static Plan LoadPlan(byte[] bytes, string planDirectory)
    {
        Plan plan = JsonSerializer.Deserialize<Plan>(bytes, Json) ?? throw new ArgumentException("Missing plan.");
        if (plan.Version != 1 || !plan.SyntheticFixturesOnly || plan.Cases is not { Length: > 0 and <= 1000 }
            || plan.Gate is not ("P0-A" or "P0-B" or "FocusedChecks")) throw new ArgumentException("Invalid plan header.");
        string root = Path.GetFullPath(plan.FixtureRoot, planDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(root) || !root.Split(Path.DirectorySeparatorChar).Contains("testdata", StringComparer.OrdinalIgnoreCase)
            || root.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("Use a local synthetic testdata directory.");
        RejectReparsePoints(root);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var cases = new List<Case>();
        foreach (Case item in plan.Cases)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.CaseId) || !ids.Add(item.CaseId)
                || !Scenarios.Contains(item.Scenario) || string.IsNullOrWhiteSpace(item.Instruction)
                || string.IsNullOrWhiteSpace(item.WindowGroup)) throw new ArgumentException("Invalid or duplicate case.");
            bool explorer = item.Scenario.StartsWith("explorer-", StringComparison.Ordinal);
            if (plan.Gate == "P0-A" && !explorer || plan.Gate == "P0-B" && explorer)
                throw new ArgumentException("Case family does not match the gate.");
            if (explorer && string.IsNullOrWhiteSpace(item.ViewGroup) || !explorer && string.IsNullOrWhiteSpace(item.ProcessGroup))
                throw new ArgumentException("Explorer needs viewGroup; Excel needs processGroup.");
            CapturedTarget? target = item.ExpectedTarget;
            if (item.ExpectedResult == ResultCode.Captured)
            {
                if (target is null) throw new ArgumentException("Captured requires an explicit expectedTarget.");
                target = target with { Path = Path.GetFullPath(target.Path, root) };
                PathPolicy.Validate(target);
                if (!WithinRoot(target.Path, root) || !Path.Exists(target.Path)) throw new ArgumentException("Expected path must exist under fixtureRoot.");
                RejectReparsePoints(target.Path);
                if (explorer == (target.Kind == TargetKind.ExcelCell)) throw new ArgumentException("Target kind does not match scenario.");
                if (item.Scenario is "explorer-folder" or "explorer-selected-folder" && target.Kind != TargetKind.Folder
                    || item.Scenario is "explorer-file" or "explorer-same-folder-tabs" && target.Kind != TargetKind.File)
                    throw new ArgumentException("Scenario requires the documented target kind.");
                if (item.Scenario.EndsWith("-rejection", StringComparison.Ordinal)) throw new ArgumentException("Rejection cannot expect capture.");
            }
            else
            {
                bool allowed = item.Scenario switch
                {
                    "explorer-transition" => item.ExpectedResult is ResultCode.ContextChanged or ResultCode.AmbiguousTarget,
                    "explorer-rejection" => item.ExpectedResult is ResultCode.MultipleSelection or ResultCode.UnsupportedTarget,
                    "excel-rejection" => item.ExpectedResult is ResultCode.UnsavedWorkbook or ResultCode.UnsupportedTarget
                        or ResultCode.AmbiguousTarget or ResultCode.EnumerationIncomplete,
                    _ => false
                };
                if (!allowed || target is not null) throw new ArgumentException("Stable capture failures cannot be declared expected passes.");
            }
            cases.Add(item with { ExpectedTarget = target });
        }
        return plan with { FixtureRoot = root, Cases = cases.ToArray() };
    }

    internal static bool WithinRoot(string path, string root)
    {
        string full = Path.GetFullPath(path), boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return full.Equals(boundary, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static void RejectReparsePoints(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Fixture paths cannot use reparse points.");
    }

    // Pure verdict and coverage helpers are callable by the standalone regression harness.
    internal static string AssessCase(Case item, TargetSnapshot snapshot, WorkerResponse response, IReadOnlyList<Attempt> previous)
    {
        if (response.Code == ResultCode.Captured && (item.ExpectedTarget is null || response.Target is null
            || response.Target != item.ExpectedTarget)) return "WrongTarget";
        if (response.Code != item.ExpectedResult) return "UnexpectedResult";
        if (response.Code != ResultCode.Captured && response.Target is not null) return "UnexpectedTargetPayload";
        if (snapshot.Hwnd == 0 || snapshot.ProcessId == 0 || snapshot.ProcessStartTimeUtcTicks == 0) return "InvalidSnapshot";
        foreach (Attempt prior in previous.Where(a => a.Verdict == "Pass"))
        {
            if ((item.WindowGroup == prior.Case.WindowGroup) != (snapshot.Hwnd == prior.Snapshot.Hwnd
                && snapshot.ProcessId == prior.Snapshot.ProcessId && snapshot.ProcessStartTimeUtcTicks == prior.Snapshot.ProcessStartTimeUtcTicks))
                return "WindowGroupMismatch";
            if (item.ViewGroup is not null && prior.Case.ViewGroup is not null
                && ((item.ViewGroup == prior.Case.ViewGroup) != (snapshot.ActiveViewHwnd == prior.Snapshot.ActiveViewHwnd
                    && snapshot.ProcessId == prior.Snapshot.ProcessId && snapshot.ProcessStartTimeUtcTicks == prior.Snapshot.ProcessStartTimeUtcTicks)))
                return "ViewGroupMismatch";
            if (item.ProcessGroup is not null && prior.Case.ProcessGroup is not null
                && ((item.ProcessGroup == prior.Case.ProcessGroup) != (snapshot.ProcessId == prior.Snapshot.ProcessId
                    && snapshot.ProcessStartTimeUtcTicks == prior.Snapshot.ProcessStartTimeUtcTicks)))
                return "ProcessGroupMismatch";
        }
        if (item.ViewGroup is not null && snapshot.ActiveViewHwnd == 0) return "MissingActiveView";
        return "Pass";
    }

    internal static string[] MissingCoverage(Plan plan, IReadOnlyList<Attempt> attempts)
    {
        var missing = new List<string>();
        if (attempts.Count != plan.Cases.Length || attempts.Any(a => a.Verdict != "Pass")) missing.Add("Every planned case must pass.");
        var captures = attempts.Where(a => a.Verdict == "Pass" && a.Response?.Code == ResultCode.Captured && a.Case.Scenario != "explorer-transition").ToArray();
        if (plan.Gate == "FocusedChecks") { missing.Add("FocusedChecks never establishes a P0 gate."); return missing.ToArray(); }
        if (captures.Length < 50) missing.Add("At least 50 exact stable captures are required; expected rejections are not stable captures.");
        if (plan.Gate == "P0-A")
        {
            foreach (string scenario in new[] { "explorer-folder", "explorer-file", "explorer-selected-folder" })
                if (!captures.Any(a => a.Case.Scenario == scenario)) missing.Add("Missing " + scenario + ".");
            if (captures.GroupBy(a => a.Case.WindowGroup).Count(g => g.Select(a => a.Case.ViewGroup).Distinct().Count() >= 2) < 2)
                missing.Add("Two distinct Explorer windows, each with at least two distinct active views, are required.");
            var sameFolder = captures.Where(a => a.Case.Scenario == "explorer-same-folder-tabs").ToArray();
            if (!sameFolder.Any(a => sameFolder.Any(b => a.Case.WindowGroup == b.Case.WindowGroup
                && a.Case.ViewGroup != b.Case.ViewGroup && a.Case.ExpectedTarget!.Path != b.Case.ExpectedTarget!.Path
                && Path.GetDirectoryName(a.Case.ExpectedTarget.Path) == Path.GetDirectoryName(b.Case.ExpectedTarget.Path))))
                missing.Add("Same-folder tabs with different predeclared selected files are required.");
        }
        else
        {
            var same = captures.Where(a => a.Case.Scenario == "excel-same-process").ToArray();
            var separate = captures.Where(a => a.Case.Scenario == "excel-separate-process").ToArray();
            bool Pair(Attempt a, Attempt b) => a.Case.ExpectedTarget!.Path != b.Case.ExpectedTarget!.Path
                && Path.GetFileName(a.Case.ExpectedTarget.Path) == Path.GetFileName(b.Case.ExpectedTarget.Path);
            if (!same.Any(a => same.Any(b => Pair(a, b) && a.Case.ProcessGroup == b.Case.ProcessGroup && a.Case.WindowGroup != b.Case.WindowGroup)))
                missing.Add("Same-process distinct Excel windows with different paths and the same filename are required.");
            if (!separate.Any(a => separate.Any(b => Pair(a, b) && a.Case.ProcessGroup != b.Case.ProcessGroup)))
                missing.Add("Distinct Excel processes with different paths and the same filename are required.");
        }
        return missing.ToArray();
    }


    internal static int RunSelfTests()
    {
        int passed = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); passed++; Console.WriteLine("PASS " + name); }
        var target = new CapturedTarget(TargetKind.File, @"D:\synthetic\testdata\A\one.txt");
        var item = new Case("one", "explorer-file", "select one.txt", "w1", "v1", null, target);
        var snapshot = new TargetSnapshot(10, 100, 1000, ActiveViewHwnd: 11);
        WorkerResponse Response(CapturedTarget? result = null, ResultCode code = ResultCode.Captured) => new(1, Guid.NewGuid(), code, result);
        Attempt AttemptFor(Case c, TargetSnapshot s, WorkerResponse r) => new(1, c, s, r, "Pass", 12, DateTimeOffset.UnixEpoch);
        try
        {
            Check(AssessCase(item, snapshot, Response(target), []) == "Pass", "Exact predeclared target passes");
            Check(AssessCase(item, snapshot, Response(target with { Path = @"D:\synthetic\testdata\B\one.txt" }), []) == "WrongTarget", "Same filename in another path stops");
            Check(AssessCase(item, snapshot, Response(code: ResultCode.CaptureTimedOut), []) == "UnexpectedResult", "Stable timeout cannot pass");
            var rejected = item with { Scenario = "explorer-rejection", ExpectedTarget = null, ExpectedResult = ResultCode.MultipleSelection };
            Check(AssessCase(rejected, snapshot, Response(code: ResultCode.MultipleSelection), []) == "Pass", "Explicit rejection oracle passes its own case");
            Check(AssessCase(rejected, snapshot, Response(target), []) == "WrongTarget", "Unexpected successful capture in a rejection case stops");
            var previous = new[] { AttemptFor(item, snapshot, Response(target)) };
            Check(AssessCase(item, snapshot with { ActiveViewHwnd = 12 }, Response(target), previous) == "ViewGroupMismatch", "Same view group cannot silently switch tabs");
            Check(AssessCase(item with { ViewGroup = "v2" }, snapshot, Response(target), previous) == "ViewGroupMismatch", "Distinct tab labels cannot reuse one active view");
            var excel = new Case("excel", "excel-stable", "fixture", "e1", null, "p1", new(TargetKind.ExcelCell, @"D:\synthetic\testdata\A\same.xlsx", "Sheet1", "$A$1", false));
            var excelPrior = new[] { AttemptFor(excel, snapshot, Response(excel.ExpectedTarget)) };
            Check(AssessCase(excel with { WindowGroup = "e2" }, snapshot with { Hwnd = 20, ProcessId = 200 }, Response(excel.ExpectedTarget), excelPrior) == "ProcessGroupMismatch", "Same Excel process group cannot change PID");
            Check(!WithinRoot(@"D:\synthetic\testdata-other\one.txt", @"D:\synthetic\testdata"), "Fixture prefix sibling is outside root");
            Check(MissingCoverage(new(1, true, @"D:\synthetic\testdata", "P0-A", [item]), previous).Length > 0, "One successful capture is not a 50-case gate");
            var repeated = Enumerable.Range(0, 50).Select(i => previous[0] with { Number = i + 1 }).ToArray();
            Check(MissingCoverage(new(1, true, @"D:\synthetic\testdata", "P0-A", Enumerable.Repeat(item, 50).ToArray()), repeated).Length > 0,
                "Fifty copies of one view do not satisfy scenario coverage");
            Check(MissingCoverage(new(1, true, @"D:\synthetic\testdata", "FocusedChecks", [item]), previous).Length > 0,
                "Focused checks never establish a P0 gate");
            var transitionOnly = repeated.Select(a => a with { Case = a.Case with { Scenario = "explorer-transition" } }).ToArray();
            Check(MissingCoverage(new(1, true, @"D:\synthetic\testdata", "P0-A", transitionOnly.Select(a => a.Case).ToArray()), transitionOnly).Any(m => m.Contains("50", StringComparison.Ordinal)),
                "Exact transition captures do not inflate the stable sample count");
            Case[] seeds =
            [
                item with { Scenario = "explorer-folder", ExpectedTarget = new(TargetKind.Folder, @"D:\synthetic\testdata\A") },
                item with { Scenario = "explorer-selected-folder", ExpectedTarget = new(TargetKind.Folder, @"D:\synthetic\testdata\A\child") },
                item with { Scenario = "explorer-same-folder-tabs" },
                item with { Scenario = "explorer-same-folder-tabs", ViewGroup = "v2", ExpectedTarget = target with { Path = @"D:\synthetic\testdata\A\two.txt" } },
                item with { WindowGroup = "w2", ViewGroup = "v3" },
                item with { WindowGroup = "w2", ViewGroup = "v4" }
            ];
            var complete = Enumerable.Range(0, 50).Select(i =>
            {
                Case c = seeds[i % seeds.Length] with { CaseId = "case-" + i };
                var s = snapshot with { Hwnd = c.WindowGroup == "w1" ? 10 : 20,
                    ActiveViewHwnd = c.ViewGroup switch { "v1" => 11, "v2" => 12, "v3" => 21, _ => 22 } };
                return AttemptFor(c, s, Response(c.ExpectedTarget)) with { Number = i + 1 };
            }).ToArray();
            Check(MissingCoverage(new(1, true, @"D:\synthetic\testdata", "P0-A", complete.Select(a => a.Case).ToArray()), complete).Length == 0,
                "Complete synthetic two-window two-tab oracle can satisfy capture coverage");
            Console.WriteLine($"Capture session verdict checks: {passed} passed. Synthetic assertions only; no live P0 result.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine("FAIL " + exception.Message); return 1; }
    }

    private sealed class Session(Plan plan, string app, string output, string planHash) : ApplicationContext
    {
        private readonly List<Attempt> attempts = [];
        private readonly WorkerClient client = new(app);
        private readonly CancellationTokenSource cancellation = new();
        private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
        private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
        private readonly Dictionary<string, string> implementationHashes = HashImplementation(app);
        private bool busy;
        private int ignoredHotkeyCount;
        private string? pendingCaseId;
        private TriggerWindow? trigger;
        private string state = "Ready";
        private string? failure;
        internal bool Finished { get; private set; }
        internal int ExitCode { get; private set; } = 1;
        internal void Cancel() => cancellation.Cancel();
        internal void Start()
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            WriteEvidence();
            try { trigger = new TriggerWindow(CaptureNext); }
            catch (InvalidOperationException) { Finish("Blocked", "Ctrl+Alt+F9 is already registered; no keys were changed."); return; }
            timer.Tick += (_, _) => { if (cancellation.IsCancellationRequested && !busy) Finish("Cancelled", "Operator cancelled."); };
            timer.Start();
            Console.WriteLine("Synthetic fixtures only. The fixed plan and actual in-root paths are recorded. Ctrl+Alt+F9 captures; Ctrl+C cancels. Do not close/reopen fixture windows during a session.");
            Prompt();
        }
        private void Prompt()
        {
            Case item = plan.Cases[attempts.Count];
            Console.WriteLine($"[{attempts.Count + 1}/{plan.Cases.Length}] {item.CaseId}: {item.Instruction}");
        }
        private async void CaptureNext()
        {
            if (Finished || cancellation.IsCancellationRequested) return;
            if (busy) { ignoredHotkeyCount++; return; }
            // Snapshot comes before any output or other action in the accepted hotkey handler.
            TargetSnapshot snapshot;
            try { snapshot = ForegroundSnapshot.Capture(); }
            catch (Exception exception) { Finish("Failed", "SnapshotError:" + exception.GetType().Name); return; }
            busy = true;
            var elapsed = Stopwatch.StartNew();
            Case item = plan.Cases[attempts.Count];
            WorkerResponse? response = null;
            bool redacted = false, workerRequested = false;
            string verdict;
            pendingCaseId = item.CaseId; state = "Capturing";
            try
            {
                WriteEvidence();
                var currentHashes = HashImplementation(app);
                if (currentHashes.Count != implementationHashes.Count || implementationHashes.Any(p => !currentHashes.TryGetValue(p.Key, out string? value) || value != p.Value))
                    throw new InvalidOperationException("Product implementation changed during session.");
                string windowClass = Native.Class((nint)snapshot.Hwnd);
                bool intendedApp = item.Scenario.StartsWith("explorer-", StringComparison.Ordinal)
                    ? windowClass is "CabinetWClass" or "ExploreWClass" : windowClass == "XLMAIN";
                if (!intendedApp) verdict = "UnexpectedForegroundApplication";
                else
                {
                    workerRequested = true;
                    // Keep pumping the harness message loop; another hotkey while busy is ignored, never queued as a case.
                    response = await client.RunAsync(new(FrameProtocol.Version, Guid.NewGuid(), Operation.Capture,
                        DateTimeOffset.UtcNow.AddSeconds(3), snapshot), cancellation.Token);
                    if (response.Target is not null && !WithinRoot(response.Target.Path, plan.FixtureRoot))
                    {
                        // A mistaken user selection must not copy an unrelated document path into evidence.
                        response = response with { Target = null }; redacted = true; verdict = "WrongTargetOutsideFixtureRoot";
                    }
                    else verdict = AssessCase(item, snapshot, response, attempts);
                }
            }
            catch (Exception exception) { verdict = "HarnessError:" + exception.GetType().Name; }
            attempts.Add(new(attempts.Count + 1, item, snapshot, response, verdict, elapsed.ElapsedMilliseconds,
                DateTimeOffset.UtcNow, redacted, workerRequested));
            pendingCaseId = null; state = "Ready"; busy = false;
            try
            {
                WriteEvidence();
                Console.WriteLine($"{item.CaseId}: {verdict} ({elapsed.ElapsedMilliseconds} ms)");
                if (cancellation.IsCancellationRequested) Finish("Cancelled", "Operator cancelled; a pending result is not a completed session.");
                else if (verdict != "Pass") Finish("Failed", "Stopped at the first unexpected result; no automatic retry or later capture was issued.");
                else if (attempts.Count == plan.Cases.Length) Finish("Completed", null);
                else Prompt();
            }
            catch (Exception exception) { failure = "EvidenceWriteFailed:" + exception.GetType().Name; ExitCode = 1; Console.Error.WriteLine(failure); Stop(); }
        }
        private void Finish(string finalState, string? reason)
        {
            if (Finished) return;
            state = finalState; failure = reason;
            ExitCode = state == "Completed" && attempts.All(a => a.Verdict == "Pass") ? 0 : 1;
            try { WriteEvidence(); }
            catch (Exception exception) { ExitCode = 1; Console.Error.WriteLine("Evidence finalization failed: " + exception.GetType().Name); }
            finally { Stop(); }
        }
        private void Stop()
        {
            Finished = true; timer.Stop(); trigger?.Dispose(); trigger = null; client.Cancel(); ExitThread();
            Console.WriteLine("Session stopped. Overall P0 remains separately assessed. Evidence: " + output);
        }
        private void WriteEvidence()
        {
            string[] missing = MissingCoverage(plan, attempts);
            var evidence = new
            {
                schemaVersion = 1, startedUtc = started, updatedUtc = DateTimeOffset.UtcNow, state, failure, planSha256 = planHash,
                implementationSha256 = implementationHashes, plan, pendingCaseId, ignoredHotkeyCount, captureDeadlineMilliseconds = 3000, hotkey = "Ctrl+Alt+F9", plannedCount = plan.Cases.Length,
                attemptedCount = attempts.Count, workerRequestCount = attempts.Count(a => a.WorkerRequested),
                returnedResponseCount = attempts.Count(a => a.Response is not null),
                exactCaptureCount = attempts.Count(a => a.Verdict == "Pass" && a.Response?.Code == ResultCode.Captured),
                stableCaptureCount = attempts.Count(a => a.Verdict == "Pass" && a.Response?.Code == ResultCode.Captured && a.Case.Scenario != "explorer-transition"),
                gateEvidenceStatus = state == "Completed" && missing.Length == 0 ? "CaptureCoverageSatisfied" : "NotEstablished",
                missingCoverage = missing, overallP0Passed = false,
                limitations = "Manual synthetic-fixture capture only. No automatic activation/input/resume/DB commit. Timing is worker capture only, not capture-to-commit performance. Expected rejection is not stable success. No document content or out-of-root path is retained.",
                attempts
            };
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, evidence, Json); stream.Flush(flushToDisk: true); }
                if (File.Exists(output)) File.Replace(temporary, output, null); else File.Move(temporary, output);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static Dictionary<string, string> HashImplementation(string executable)
        {
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            string directory = Path.GetDirectoryName(executable)!;
            foreach (string file in new[] { Path.GetFileName(executable), "WorkBookmark.dll", "WorkBookmark.Core.dll", "WorkBookmark.Windows.dll", "WorkBookmark.Storage.dll" }.Distinct())
            {
                string path = Path.Combine(directory, file);
                if (!File.Exists(path)) continue;
                using var stream = File.OpenRead(path);
                hashes.Add(file, Convert.ToHexString(SHA256.HashData(stream)));
            }
            return hashes;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { trigger?.Dispose(); timer.Dispose(); client.Dispose(); cancellation.Dispose(); }
            base.Dispose(disposing);
        }
    }
    private sealed class TriggerWindow : NativeWindow, IDisposable
    {
        private readonly Action capture;
        internal TriggerWindow(Action onCapture)
        {
            capture = onCapture;
            CreateHandle(new CreateParams { Caption = "WorkBookmark.ManualCaptureSession", Parent = new IntPtr(-3) });
            if (!RegisterHotKey(Handle, 1, 0x4003, (uint)Keys.F9)) { DestroyHandle(); throw new InvalidOperationException("Hotkey unavailable."); }
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0312 && message.WParam.ToInt32() == 1) { capture(); return; }
            base.WndProc(ref message);
        }
        public void Dispose() { if (Handle != IntPtr.Zero) { UnregisterHotKey(Handle, 1); DestroyHandle(); } }
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    }
}
