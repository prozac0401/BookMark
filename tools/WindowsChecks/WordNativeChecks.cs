using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using WorkBookmark.App;
using WorkBookmark.Core;
using WorkBookmark.Windows;

// Explicit generated-fixture driver: metadata and caret navigation only, never text, Save, Close or Quit.
internal static class WordNativeChecks
{
    internal static int Run(string[] args)
    {
        if (args.Length is not (5 or 6) || args.Length == 6 && args[5] is not ("read-only" or "reading-restriction"))
        { Console.Error.WriteLine("word-native-checks APP_EXE HWND SYNTHETIC_DOCX OUTPUT_JSON [read-only|reading-restriction]"); return 2; }
        var mode = args.Length == 6 ? args[5] : "editable";
        var evidence = new List<object>();
        var diagnostics = new List<object>();
        var started = DateTimeOffset.UtcNow;
        string? beforeHash = null, afterHash = null, failure = null;
        var output = Path.GetFullPath(args[4]);
        if (File.Exists(output)) { Console.Error.WriteLine("Choose a new evidence output file; prior results are never overwritten."); return 2; }
        var fixture = Path.GetFullPath(args[3]);
        try
        {
            var root = FindRepositoryRoot();
            var expectedFixture = Path.GetFullPath(Path.Combine(root, "testdata", "generated", mode switch { "read-only" => "office-readonly", "reading-restriction" => "office-restricted-readonly", _ => "office-extension" }, "workbookmark-word-fixture.docx"));
            if (!string.Equals(fixture, expectedFixture, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only the designated generated Word fixture for this test mode in this repository is allowed.");
            RejectReparsePoints(fixture);
            var app = Path.GetFullPath(args[1]);
            var hwnd = (nint)long.Parse(args[2]);
            if (!File.Exists(app) || !Native.IsWindow(hwnd) || Native.Class(hwnd) != "OpusApp")
                throw new ArgumentException("Executable or Word document window is unavailable.");
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (!Native.IsUnelevatedProcess(pid)) throw new ArgumentException("Fixture Word process must be unelevated.");
            beforeHash = Hash(fixture);
            var normalized = PathPolicy.Normalize(fixture);
            using var scope = new ComScope();
            var panes = Native.Children(hwnd, "_WwG", true);
            if (panes.Count != 1) throw new InvalidOperationException("Use one visible Word document pane.");
            var dispatch = new Guid("00020400-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(Native.AccessibleObjectFromWindow(panes[0], unchecked((uint)-16), ref dispatch, out var window));
            scope.Keep(window);
            var returnedHandle = Convert.ToInt64(scope.Get(window, "Hwnd"));
            if ((returnedHandle < 0 ? (nint)unchecked((uint)returnedHandle) : (nint)returnedHandle) != hwnd)
                throw new InvalidOperationException("Native Word window identity differs.");
            var document = scope.Get(window, "Document");
            var readOnly = scope.Flag(document, "ReadOnly");
            var protection = scope.Number(document, "ProtectionType");
            diagnostics.Add(new { stage = "document-mode", mode, readOnly, protection });
            if (PathPolicy.Normalize(scope.Text(document, "FullName")) != normalized || !scope.Flag(document, "Saved") ||
                protection != (mode == "reading-restriction" ? 3 : -1) || mode == "read-only" && !readOnly)
                throw new InvalidOperationException("Window must contain the exact unchanged synthetic fixture in the requested mode; no navigation performed.");
            var body = scope.Get(document, "Content");
            var documentEnd = scope.Number(body, "End");
            const int wanted = 37;
            if (documentEnd <= wanted || documentEnd == int.MaxValue) throw new InvalidDataException("Fixture must contain offset 37 and a bounded document end.");
            if (!Native.SetForegroundWindow(hwnd)) throw new InvalidOperationException("Test foreground activation denied.");
            scope.Call(window, "Activate");
            SelectOffset(wanted);
            Application.DoEvents(); Thread.Sleep(120);
            if (Native.GetForegroundWindow() != hwnd) throw new InvalidOperationException("Test foreground changed before capture.");
            using var client = new WorkerClient(app);
            var capture = client.RunAsync(new(1, Guid.NewGuid(), Operation.Capture, DateTimeOffset.UtcNow.AddSeconds(5), ForegroundSnapshot.Capture())).GetAwaiter().GetResult();
            if (capture.Code != ResultCode.Captured)
            {
                try
                {
                    var snapshot = ForegroundSnapshot.Capture();
                    WordAdapter.Capture(snapshot, new RequestContext(new(1, Guid.NewGuid(), Operation.Capture, DateTimeOffset.UtcNow.AddSeconds(5), snapshot)));
                }
                catch (Exception error) { diagnostics.Add(new { stage = "direct-capture", exception = error.ToString() }); }
            }
            Check(capture.Code == ResultCode.Captured && capture.Target is { } captured && captured.Kind == TargetKind.WordPosition &&
                PathPolicy.Normalize(captured.Path) == normalized && captured.WordStart == wanted && captured.HadUnsavedChanges == false,
                "capture records the independently requested synthetic body offset 37", capture);
            SelectOffset(0);
            Application.DoEvents();
            if (ReadOffset() != 0) throw new InvalidOperationException("Deliberate navigation away was not observed.");
            var resume = client.RunAsync(new(1, Guid.NewGuid(), Operation.Resume, DateTimeOffset.UtcNow.AddSeconds(15), ForegroundSnapshot.Capture(), capture.Target)).GetAwaiter().GetResult();
            if (resume.Code == ResultCode.EnumerationIncomplete)
            {
                WordAdapter.ProbeTrace = message => diagnostics.Add(new { stage = "resume-enumeration", message });
                try
                {
                    WordAdapter.Resume(capture.Target!, new RequestContext(new(1, Guid.NewGuid(), Operation.ValidateRelink,
                        DateTimeOffset.UtcNow.AddSeconds(5), ForegroundSnapshot.Capture(), capture.Target)), validateOnly: true);
                }
                catch (Exception error) { diagnostics.Add(new { stage = "resume-enumeration", exception = error.ToString() }); }
                finally { WordAdapter.ProbeTrace = null; }
            }
            Check((resume.Code is ResultCode.PositionRestored or ResultCode.PositionRestoredFocusPending) && ReadOffset() == wanted,
                "resume restores offset 37 after deliberate navigation to zero", resume);
            var outside = capture.Target! with { WordStart = documentEnd + 1 };
            var rejected = client.RunAsync(new(1, Guid.NewGuid(), Operation.Resume, DateTimeOffset.UtcNow.AddSeconds(15), ForegroundSnapshot.Capture(), outside)).GetAwaiter().GetResult();
            Check(rejected.Code == ResultCode.OpenedPositionFailed && !rejected.ExternalActionStarted && ReadOffset() == wanted,
                "offset beyond document end refuses navigation without external action", rejected);
            afterHash = Hash(fixture);
            Check(beforeHash == afterHash && scope.Flag(document, "Saved") && scope.Number(body, "End") == documentEnd &&
                scope.Flag(document, "ReadOnly") == readOnly && scope.Number(document, "ProtectionType") == protection,
                "fixture bytes, Saved state, read-only/protection state and document length remain unchanged", null);

            void SelectOffset(int position)
            {
                if (PathPolicy.Normalize(scope.Text(document, "FullName")) != normalized || !scope.Flag(document, "Saved"))
                    throw new InvalidOperationException("Synthetic fixture changed; stopping navigation.");
                var range = scope.Call(document, "Range", position, position);
                if (scope.Number(range, "Start") != position || scope.Number(range, "End") != position || scope.Number(range, "StoryType") != 1)
                    throw new InvalidOperationException("Word did not create the requested collapsed main-story range.");
                scope.Call(range, "Select");
            }
            int ReadOffset()
            {
                var selection = scope.Get(window, "Selection");
                var start = scope.Number(selection, "Start");
                return scope.Number(selection, "StoryType") == 1 && scope.Number(selection, "End") == start ? start : -1;
            }
        }
        catch (Exception error) { failure = error.GetType().Name + ": " + error.Message; }
        finally
        {
            try { if (beforeHash is not null) afterHash ??= Hash(fixture); } catch { }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(new {
                test = "Word synthetic capture and resume", mode, startedUtc = started, finishedUtc = DateTimeOffset.UtcNow,
                beforeSha256 = beforeHash, afterSha256 = afterHash, passed = failure is null && beforeHash is not null && beforeHash == afterHash,
                failure, checks = evidence, diagnostics,
                limitations = "One native Word window and saved .docx. Does not establish multiple windows, edit drift, headers/footnotes, .doc/.docm/.rtf, Office 32-bit, protected view, or 50-capture gate. No document text read, document saved/closed, or process terminated."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine(JsonSerializer.Serialize(new { output, failure, checks = evidence.Count }));
        return failure is null ? 0 : 1;

        void Check(bool passed, string name, WorkerResponse? response)
        {
            evidence.Add(new { name, passed, response });
            if (!passed) throw new InvalidOperationException(name);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "WorkBookmark.sln"))) return directory.FullName;
        throw new ArgumentException("Run from this WorkBookmark repository or one of its subdirectories.");
    }

    private static void RejectReparsePoints(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Synthetic fixture path must not contain a reparse point.");
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
