using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using WorkBookmark.App;
using WorkBookmark.Core;
using WorkBookmark.Windows;

// Deliberately synthetic-only test harness; this is not part of product startup or worker code.
internal static class ExcelSeries
{
    private sealed record View(string Path, uint Pid, long Hwnd, string Sheet, string Cell, bool Unsaved);
    // ElapsedMilliseconds retains the old whole-attempt duration; worker time excludes driver activation.
    private sealed record Attempt(int Number, string ExpectedPath, string ExpectedSheet, string ExpectedCell, uint ExpectedPid, long ExpectedHwnd, TargetSnapshot? Snapshot, WorkerResponse? Response, string Result, long ElapsedMilliseconds, long? WorkerElapsedMilliseconds, string? Failure);
    internal sealed record EvidenceSample(string Result, bool WorkerResponded, long? WorkerElapsedMilliseconds);
    internal sealed record EvidenceVerdict(string GateStatus, string SeparateProcessCaseStatus, bool FileBytesUnchanged, string HashVerificationStatus, int SuccessfulWorkerSampleCount, long? SuccessfulWorkerP95Milliseconds, int FailureCount, string Explanation);

    // Pure verdict calculation: no UI, COM, filesystem, clocks, or product execution.
    internal static EvidenceVerdict EvaluateEvidence(int requestedCount, IReadOnlyList<EvidenceSample> samples,
        IReadOnlyDictionary<string, string?> beforeHashes, IReadOnlyDictionary<string, string?> afterHashes, string? failure, bool completed)
    {
        bool hashesReadable = beforeHashes.Count == 2 && afterHashes.Count == 2 && beforeHashes.All(pair => pair.Value is not null && afterHashes.TryGetValue(pair.Key, out var after) && after is not null);
        bool unchanged = hashesReadable && beforeHashes.All(pair => pair.Value == afterHashes[pair.Key]);
        var times = samples.Where(sample => sample.Result == "Pass" && sample.WorkerResponded && sample.WorkerElapsedMilliseconds is >= 0)
            .Select(sample => sample.WorkerElapsedMilliseconds!.Value).Order().ToArray();
        int attemptFailures = samples.Count(sample => sample.Result != "Pass");
        bool allPassed = completed && failure is null && unchanged && requestedCount is >= 1 and <= 50 && samples.Count == requestedCount && samples.All(sample => sample.Result == "Pass" && sample.WorkerResponded && sample.WorkerElapsedMilliseconds is >= 0);
        bool blocked = samples.All(sample => !sample.WorkerResponded);
        string caseStatus = allPassed ? requestedCount == 50 ? "Passed" : "PartialSample"
            : failure is null && unchanged && attemptFailures == 0 && !completed ? "InProgress"
            : blocked ? "Blocked" : "NotPassed";
        string explanation = allPassed ? requestedCount == 50
            ? "Fifty separate-process captures passed with unchanged fixture bytes. Same-process multi-window and the remaining P0-B cases still require evidence."
            : "The requested sample passed, but fewer than fifty captures cannot establish even the separate-process case."
            : !unchanged ? "Fixture hashes are unavailable or changed; unchanged file bytes are not established."
            : failure is not null ? "The run recorded a failure; successful earlier captures do not override it."
            : !completed ? "The series is incomplete."
            : "One or more attempts did not produce a verified successful worker capture.";
        // The generic gate describes full P0-B. This harness can establish only its separate-process case.
        return new(blocked ? "Blocked" : "NotPassed", caseStatus, unchanged, !hashesReadable ? "Unavailable" : unchanged ? "Unchanged" : "Changed",
            times.Length, times.Length == 0 ? null : times[(int)Math.Ceiling(times.Length * .95) - 1], Math.Max(attemptFailures, failure is null ? 0 : 1), explanation);
    }

    internal static int Run(string[] args)
    {
        if (args.Length < 5) { Console.Error.WriteLine("series APP_EXE EXCEL_EXE FIXTURE_ROOT OUTPUT_JSON [COUNT=50] [A_CELL=$D$127] [B_CELL=$F$42]"); return 2; }
        // Once an output path is available, validation and preparation failures are evidence too.
        string output = Path.GetFullPath(args[4]);
        int count = 50;
        string[] paths = [];
        var initialHashes = new Dictionary<string, string?>();
        var attempts = new List<Attempt>();
        var started = DateTimeOffset.UtcNow;
        string? failure = null;
        string phase = "Validating";
        bool completed = false;
        var launches = new List<int>();
        try
        {
            string app = Path.GetFullPath(args[1]), excel = Path.GetFullPath(args[2]), fixtureRoot = Path.GetFullPath(args[3]);
            count = args.Length > 5 ? int.Parse(args[5]) : 50;
            if (count < 1 || count > 50 || !File.Exists(app) || !File.Exists(excel)) throw new ArgumentException("Invalid executable/count.");
            // Never accept arbitrary target documents: these two specific synthetic fixture paths are the only candidates.
            if (!fixtureRoot.Replace('/', '\\').EndsWith(@"\testdata\generated", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only repository testdata/generated fixtures are allowed.");
            paths = [Path.Combine(fixtureRoot, "A", "같은이름.xlsx"), Path.Combine(fixtureRoot, "B", "같은이름.xlsx")];
            string[] cells = [args.Length > 6 ? args[6] : "$D$127", args.Length > 7 ? args[7] : "$F$42"];
            foreach (var (path, cell) in paths.Zip(cells)) { if (!File.Exists(path)) throw new FileNotFoundException("Generate synthetic fixtures first."); PathPolicy.Validate(new(TargetKind.ExcelCell, path, "확정자", cell, false)); }
            var allowed = paths.Select(PathPolicy.Normalize).ToHashSet(StringComparer.Ordinal);
            initialHashes = paths.ToDictionary(p => p, ReadHash);
            if (initialHashes.Values.Any(hash => hash is null)) throw new IOException("Fixture baseline hash is unavailable; no Excel activation was attempted.");
            phase = "Preparing";
            if (!WriteEvidence().FileBytesUnchanged) throw new IOException("Fixture bytes changed or became unreadable during preparation.");
            // A known, complete inventory must contain no unrelated documents before any /x launch or focus request.
            var inventory = Inventory(allowed);
            for (int index = 0; index < paths.Length; index++)
            {
                var matches = inventory.Where(v => v.Path == PathPolicy.Normalize(paths[index])).ToList();
                if (matches.Select(v => v.Pid).Distinct().Count() > 1) throw new InvalidOperationException("Synthetic path already has ambiguous instances.");
                if (matches.Count != 0) continue;
                var start = new ProcessStartInfo(excel) { UseShellExecute = false, CreateNoWindow = false };
                start.ArgumentList.Add("/x"); start.ArgumentList.Add(paths[index]);
                // Test-only user-style launch. No Excel object-model Open/Save/Close/Quit is used.
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Excel did not start.");
                launches.Add(process.Id);
                var deadline = Stopwatch.StartNew();
                while (true)
                {
                    if (deadline.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Excel fixture native model did not become ready.");
                    Thread.Sleep(150); Application.DoEvents();
                    try { inventory = Inventory(allowed); }
                    catch (InvalidOperationException) { continue; } // Observation only; launch is never repeated.
                    if (inventory.Any(v => v.Path == PathPolicy.Normalize(paths[index]))) break;
                }
            }
            inventory = Inventory(allowed);
            var selected = paths.Select(p => inventory.Where(v => v.Path == PathPolicy.Normalize(p)).OrderBy(v => v.Hwnd).FirstOrDefault() ?? throw new InvalidOperationException("Missing synthetic fixture window.")).ToArray();
            if (selected[0].Pid == selected[1].Pid) throw new InvalidOperationException("Separate Excel process IDs are required; no ambiguous duplicate is opened automatically.");
            for (int index = 0; index < 2; index++)
                if (selected[index].Sheet != "확정자" || selected[index].Cell != cells[index] || selected[index].Unsaved)
                    throw new InvalidOperationException("Fixture starting sheet/cell/unsaved state differs from the explicit expected oracle. No selection or document edits were applied.");
            using var client = new WorkerClient(app);
            phase = "Capturing";
            for (int index = 0; index < count; index++)
            {
                var view = selected[index % 2];
                var elapsed = Stopwatch.StartNew();
                Stopwatch? workerElapsed = null;
                WorkerResponse? response = null; TargetSnapshot? snapshot = null; string result;
                string? attemptFailure = null;
                try
                {
                    Native.SetForegroundWindow((nint)view.Hwnd); Thread.Sleep(120); Application.DoEvents();
                    if (Native.GetForegroundWindow() != (nint)view.Hwnd) result = "ForegroundDenied";
                    else
                    {
                        snapshot = ForegroundSnapshot.Capture();
                        if (snapshot.Hwnd != view.Hwnd || snapshot.ProcessId != view.Pid) result = "PreCaptureContextChanged";
                        else
                        {
                            workerElapsed = Stopwatch.StartNew();
                            try { response = client.RunAsync(new(1, Guid.NewGuid(), Operation.Capture, DateTimeOffset.UtcNow.AddSeconds(3), snapshot)).GetAwaiter().GetResult(); }
                            finally { workerElapsed.Stop(); }
                            if (response.Code != ResultCode.Captured) result = response.Code.ToString();
                            else
                            {
                                var actual = response.Target;
                                result = actual is not null && actual.Kind == TargetKind.ExcelCell && PathPolicy.Normalize(actual.Path) == view.Path && actual.SheetName == "확정자" && actual.CellAddress == cells[index % 2] && actual.HadUnsavedChanges == false ? "Pass" : "WrongTarget";
                            }
                        }
                    }
                }
                catch (Exception exception)
                {
                    // Preserve an interrupted attempt, including a worker exception or invalid returned identity.
                    result = response?.Code == ResultCode.Captured ? "WrongTarget" : workerElapsed is null ? "DriverException" : "WorkerException";
                    attemptFailure = exception.GetType().Name + ": " + exception.Message;
                    failure = attemptFailure;
                }
                attempts.Add(new(index + 1, view.Path, "확정자", cells[index % 2], view.Pid, view.Hwnd, snapshot, response, result, elapsed.ElapsedMilliseconds, workerElapsed?.ElapsedMilliseconds, attemptFailure));
                if (result == "WrongTarget") failure ??= "Wrong target observed; series stopped immediately.";
                if (result == "ForegroundDenied") failure ??= "Test-driver foreground activation denied; no further activations attempted.";
                if (!WriteEvidence().FileBytesUnchanged) failure ??= "Fixture bytes changed or became unreadable; series stopped immediately.";
                if (failure is not null) break;
            }
            completed = failure is null && attempts.Count == count;
        }
        catch (Exception exception) { failure = exception.GetType().Name + ": " + exception.Message; }
        phase = failure is not null ? "Failed" : completed ? "Completed" : "Incomplete";
        var verdict = WriteEvidence();
        Console.WriteLine(JsonSerializer.Serialize(new { output, attempts = attempts.Count, pass = attempts.Count(a => a.Result == "Pass"), wrongTarget = attempts.Count(a => a.Result == "WrongTarget"), failure, verdict.GateStatus, verdict.SeparateProcessCaseStatus }));
        // Exit zero means the requested sample succeeded; the JSON distinguishes a partial sample from 50-case evidence.
        return verdict.SeparateProcessCaseStatus is "Passed" or "PartialSample" ? 0 : 1;

        EvidenceVerdict WriteEvidence()
        {
            var currentHashes = paths.ToDictionary(p => p, ReadHash);
            var verdict = EvaluateEvidence(count, attempts.Select(a => new EvidenceSample(a.Result, a.Response is not null, a.WorkerElapsedMilliseconds)).ToArray(), initialHashes, currentHashes, failure, completed);
            if (!verdict.FileBytesUnchanged && failure is null)
            {
                failure = "Fixture bytes changed or a required hash became unreadable; series cannot pass.";
                completed = false;
                verdict = EvaluateEvidence(count, attempts.Select(a => new EvidenceSample(a.Result, a.Response is not null, a.WorkerElapsedMilliseconds)).ToArray(), initialHashes, currentHashes, failure, completed);
            }
            if (failure is not null) phase = "Failed";
            var evidence = new
            {
                schemaVersion = 2,
                test = "P0-B separate-process same-name synthetic workbook capture series",
                startedUtc = started, lastUpdatedUtc = DateTimeOffset.UtcNow, requestedCount = count,
                phase, runCompleted = completed, partialSample = count is > 0 and < 50,
                workerCaptureCount = attempts.Count(a => a.Response is not null),
                gateStatus = verdict.GateStatus, gateScope = "Full P0-B; this series alone cannot establish it",
                separateProcessCaseStatus = verdict.SeparateProcessCaseStatus,
                passCount = attempts.Count(a => a.Result == "Pass"), wrongTargetCount = attempts.Count(a => a.Result == "WrongTarget"),
                failureCount = verdict.FailureCount, attemptFailureCount = attempts.Count(a => a.Result != "Pass"), runFailureCount = failure is null ? 0 : 1,
                // Retain the old numeric field; zero without samples means unavailable, as the new nullable field makes explicit.
                p95Milliseconds = verdict.SuccessfulWorkerP95Milliseconds ?? 0,
                successfulWorkerP95Milliseconds = verdict.SuccessfulWorkerP95Milliseconds,
                successfulWorkerSampleCount = verdict.SuccessfulWorkerSampleCount,
                timingScope = "Successful verified WorkerClient Capture round trip only; excludes foreground activation, failed attempts, and database commit. Not product capture-commit p95.",
                failure, launchedProcessIds = launches, beforeSha256 = initialHashes, afterSha256 = currentHashes,
                fileBytesUnchanged = verdict.FileBytesUnchanged, hashVerificationStatus = verdict.HashVerificationStatus,
                explanation = verdict.Explanation,
                limitations = "Only separate-process stable capture. Does not establish full P0-B, same-process multi-window, every format, DRM, Explorer tab gates, or capture-commit performance. No Excel process closed or killed.",
                attempts
            };
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            string temporary = output + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, output, true);
            return verdict;
        }
    }

    internal static int RunEvidenceSelfTests()
    {
        var hashes = new Dictionary<string, string?> { ["A"] = "AA", ["B"] = "BB" };
        var fifty = Enumerable.Range(1, 50).Select(ms => new EvidenceSample("Pass", true, ms)).ToArray();
        int checks = 0;
        void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException("Evidence self-test failed: " + description); checks++; }
        EvidenceVerdict Evaluate(IReadOnlyList<EvidenceSample> samples, int count = 50, string? failure = null, bool completed = true, IReadOnlyDictionary<string, string?>? after = null)
            => EvaluateEvidence(count, samples, hashes, after ?? hashes, failure, completed);
        var full = Evaluate(fifty);
        Check(full.SeparateProcessCaseStatus == "Passed" && full.GateStatus == "NotPassed", "fifty successes establish only the separate-process case");
        Check(full.SuccessfulWorkerSampleCount == 50 && full.SuccessfulWorkerP95Milliseconds == 48, "nearest-rank p95 uses successful worker durations");
        Check(Evaluate(fifty.Take(3).ToArray(), count: 3).SeparateProcessCaseStatus == "PartialSample", "short requested runs remain partial samples");
        Check(Evaluate(fifty, failure: "finalization failed").SeparateProcessCaseStatus == "NotPassed", "run failures veto successful attempts");
        var changed = Evaluate(fifty, after: new Dictionary<string, string?> { ["A"] = "changed", ["B"] = "BB" });
        Check(changed.HashVerificationStatus == "Changed" && changed.SeparateProcessCaseStatus == "NotPassed", "changed bytes veto success");
        Check(Evaluate(fifty, after: new Dictionary<string, string?> { ["A"] = null, ["B"] = "BB" }).SeparateProcessCaseStatus == "NotPassed", "unreadable final hash vetoes success");
        Check(EvaluateEvidence(50, fifty, new Dictionary<string, string?> { ["A"] = null, ["B"] = "BB" }, hashes, null, true).SeparateProcessCaseStatus == "NotPassed", "unreadable baseline hash vetoes success");
        var blocked = Evaluate([new("ForegroundDenied", false, 9000)], failure: "activation denied");
        Check(blocked.GateStatus == "Blocked" && blocked.SuccessfulWorkerSampleCount == 0 && blocked.SuccessfulWorkerP95Milliseconds is null, "activation failures provide no worker timing samples");
        var mixed = Evaluate([new("Pass", true, 17), new("WrongTarget", true, 9000), new("WorkerException", false, 8000), new("ForegroundDenied", false, null)]);
        Check(mixed.SeparateProcessCaseStatus == "NotPassed" && mixed.SuccessfulWorkerP95Milliseconds == 17 && mixed.SuccessfulWorkerSampleCount == 1, "wrong targets and failed attempts never enter successful worker p95");
        Check(Evaluate([], failure: "startup failed", completed: false).FailureCount == 1, "startup failure survives with no attempts");
        Check(Evaluate(fifty.Take(49).ToArray()).SeparateProcessCaseStatus == "NotPassed", "incomplete attempt count cannot pass");
        Check(Evaluate(fifty, completed: false).SeparateProcessCaseStatus == "InProgress", "last periodic write cannot claim completion before finalization");
        Check(Evaluate([new("Pass", true, null)], count: 1).SeparateProcessCaseStatus == "NotPassed", "missing worker measurement cannot produce a successful sample");
        Console.WriteLine($"PASS ExcelSeries evidence verdict: {checks} checks; no Office or UI interaction.");
        return 0;
    }

    private static string? ReadHash(string path)
    {
        try { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return Convert.ToHexString(SHA256.HashData(stream)); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static List<View> Inventory(HashSet<string> allowed)
    {
        using var scope = new ComScope();
        var applications = new Dictionary<nint, object>(); var covered = new HashSet<uint>(); var deferred = new HashSet<uint>();
        foreach (var root in Native.ExcelRoots())
        {
            Native.GetWindowThreadProcessId(root, out var pid);
            var children = Native.Children(root, "EXCEL7", false);
            if (children.Count == 0 && !Native.IsWindowVisible(root)) { deferred.Add(pid); continue; }
            if (children.Count == 0) throw new InvalidOperationException("A visible Excel window has no available native document model.");
            foreach (var child in children)
            {
                var iid = new Guid("00020400-0000-0000-C000-000000000046");
                if (Native.AccessibleObjectFromWindow(child, unchecked((uint)-16), ref iid, out var window) < 0) throw new InvalidOperationException("An Excel native model is inaccessible.");
                scope.Keep(window); var application = scope.Get(window, "Application");
                applications[ComScope.Identity(application)] = application; covered.Add(pid);
            }
        }
        using var own = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName("EXCEL"))
        {
            using (process) { if (process.SessionId == own.SessionId && !covered.Contains((uint)process.Id)) throw new InvalidOperationException("An Excel process is not completely connected."); }
        }
        if (deferred.Any(pid => !covered.Contains(pid))) throw new InvalidOperationException("A hidden Excel process is not connected.");
        var result = new List<View>();
        foreach (var application in applications.Values)
        {
            if (scope.Number(scope.Get(application, "ProtectedViewWindows"), "Count") != 0) throw new InvalidOperationException("Protected-view document present; runner refuses to proceed.");
            var books = scope.Get(application, "Workbooks"); var count = scope.Number(books, "Count");
            for (var index = 1; index <= count; index++)
            {
                var book = scope.Get(books, "Item", index);
                if (string.IsNullOrEmpty(scope.Text(book, "Path"))) throw new InvalidOperationException("An unrelated unsaved workbook is open.");
                var path = PathPolicy.Normalize(scope.Text(book, "FullName"));
                if (!allowed.Contains(path)) throw new InvalidOperationException("An unrelated workbook is open; its identity is not logged and its window is untouched.");
                var windows = scope.Get(book, "Windows");
                for (var windowIndex = 1; windowIndex <= scope.Number(windows, "Count"); windowIndex++)
                {
                    var window = scope.Get(windows, "Item", windowIndex); var raw = Convert.ToInt64(scope.Get(window, "Hwnd")); var hwnd = raw < 0 ? (nint)unchecked((uint)raw) : (nint)raw;
                    if (!Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd)) continue;
                    Native.GetWindowThreadProcessId(hwnd, out var pid);
                    var sheet = scope.Get(window, "ActiveSheet"); var cell = scope.Get(window, "ActiveCell");
                    if (!ComScope.Same(scope.Get(sheet, "Parent"), book) || !ComScope.Same(scope.Get(cell, "Parent"), sheet)) throw new InvalidOperationException("A fixture parent chain changed.");
                    result.Add(new(path, pid, hwnd.ToInt64(), scope.Text(sheet, "Name"), scope.Text(cell, "Address", true, true, 1, false, Type.Missing), !scope.Flag(book, "Saved")));
                }
            }
            if (scope.Number(books, "Count") != count) throw new InvalidOperationException("Workbook inventory changed during preparation.");
        }
        return result;
    }
}
