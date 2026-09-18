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
    private sealed record Attempt(int Number, string ExpectedPath, string ExpectedSheet, string ExpectedCell, uint ExpectedPid, long ExpectedHwnd, TargetSnapshot? Snapshot, WorkerResponse? Response, string Result, long ElapsedMilliseconds);
    internal static int Run(string[] args)
    {
        if (args.Length < 5) { Console.Error.WriteLine("series APP_EXE EXCEL_EXE FIXTURE_ROOT OUTPUT_JSON [COUNT=50] [A_CELL=$D$127] [B_CELL=$F$42]"); return 2; }
        string app = Path.GetFullPath(args[1]), excel = Path.GetFullPath(args[2]), fixtureRoot = Path.GetFullPath(args[3]), output = Path.GetFullPath(args[4]);
        int count = args.Length > 5 ? int.Parse(args[5]) : 50;
        if (count < 1 || count > 50 || !File.Exists(app) || !File.Exists(excel)) throw new ArgumentException("Invalid executable/count.");
        // Never accept arbitrary target documents: these two specific synthetic fixture paths are the only candidates.
        if (!fixtureRoot.Replace('/', '\\').EndsWith(@"\testdata\generated", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only repository testdata/generated fixtures are allowed.");
        string[] paths = [Path.Combine(fixtureRoot, "A", "같은이름.xlsx"), Path.Combine(fixtureRoot, "B", "같은이름.xlsx")];
        string[] cells = [args.Length > 6 ? args[6] : "$D$127", args.Length > 7 ? args[7] : "$F$42"];
        foreach (var (path, cell) in paths.Zip(cells)) { if (!File.Exists(path)) throw new FileNotFoundException("Generate synthetic fixtures first."); PathPolicy.Validate(new(TargetKind.ExcelCell, path, "확정자", cell, false)); }
        var allowed = paths.Select(PathPolicy.Normalize).ToHashSet(StringComparer.Ordinal);
        var initialHashes = paths.ToDictionary(p => p, p => ReadHash(p));
        var attempts = new List<Attempt>();
        var started = DateTimeOffset.UtcNow;
        string? failure = null;
        var launches = new List<int>();
        try
        {
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
            for (int index = 0; index < count; index++)
            {
                var view = selected[index % 2];
                var elapsed = Stopwatch.StartNew();
                WorkerResponse? response = null; TargetSnapshot? snapshot = null; string result;
                Native.SetForegroundWindow((nint)view.Hwnd); Thread.Sleep(120); Application.DoEvents();
                if (Native.GetForegroundWindow() != (nint)view.Hwnd) result = "ForegroundDenied";
                else
                {
                    snapshot = ForegroundSnapshot.Capture();
                    if (snapshot.Hwnd != view.Hwnd || snapshot.ProcessId != view.Pid) result = "PreCaptureContextChanged";
                    else
                    {
                        response = client.RunAsync(new(1, Guid.NewGuid(), Operation.Capture, DateTimeOffset.UtcNow.AddSeconds(3), snapshot)).GetAwaiter().GetResult();
                        if (response.Code != ResultCode.Captured) result = response.Code.ToString();
                        else
                        {
                            var actual = response.Target;
                            result = actual is not null && actual.Kind == TargetKind.ExcelCell && PathPolicy.Normalize(actual.Path) == view.Path && actual.SheetName == "확정자" && actual.CellAddress == cells[index % 2] && actual.HadUnsavedChanges == false ? "Pass" : "WrongTarget";
                        }
                    }
                }
                attempts.Add(new(index + 1, view.Path, "확정자", cells[index % 2], view.Pid, view.Hwnd, snapshot, response, result, elapsed.ElapsedMilliseconds));
                WriteEvidence();
                if (result == "WrongTarget") throw new InvalidOperationException("Wrong target observed; series stopped immediately.");
                if (result == "ForegroundDenied") { failure = "Test-driver foreground activation denied; no further activations attempted."; break; }
            }
        }
        catch (Exception exception) { failure = exception.GetType().Name + ": " + exception.Message; }
        WriteEvidence();
        Console.WriteLine(JsonSerializer.Serialize(new { output, attempts = attempts.Count, pass = attempts.Count(a => a.Result == "Pass"), wrongTarget = attempts.Count(a => a.Result == "WrongTarget"), failure }));
        return failure is null && attempts.Count == count && attempts.All(a => a.Result == "Pass") ? 0 : 1;

        void WriteEvidence()
        {
            var currentHashes = paths.ToDictionary(p => p, p => ReadHash(p));
            var samples = attempts.Select(a => a.ElapsedMilliseconds).Order().ToArray();
            var evidence = new { test = "P0-B separate-process same-name synthetic workbook capture series", startedUtc = started, lastUpdatedUtc = DateTimeOffset.UtcNow, requestedCount = count, workerCaptureCount = attempts.Count(a => a.Response is not null), gateStatus = attempts.Count == count && attempts.All(a => a.Result == "Pass") ? "Passed" : attempts.All(a => a.Response is null) ? "Blocked" : "NotPassed", passCount = attempts.Count(a => a.Result == "Pass"), wrongTargetCount = attempts.Count(a => a.Result == "WrongTarget"), failureCount = attempts.Count(a => a.Result != "Pass"), p95Milliseconds = samples.Length == 0 ? 0 : samples[(int)Math.Ceiling(samples.Length * .95) - 1], failure, launchedProcessIds = launches, beforeSha256 = initialHashes, afterSha256 = currentHashes, fileBytesUnchanged = paths.All(p => initialHashes[p] is not null && initialHashes[p] == currentHashes[p]), limitations = "Only separate-process stable capture. Does not establish same-process multi-window, every format, DRM, or Explorer tab gates. No Excel process closed or killed.", attempts };
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }
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
