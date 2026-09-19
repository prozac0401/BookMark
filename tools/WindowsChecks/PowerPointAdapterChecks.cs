using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using WorkBookmark.App;
using WorkBookmark.Core;
using WorkBookmark.Windows;

// Explicit synthetic-fixture test driver. Never creates, edits, saves, closes, or quits Office documents.
internal static class PowerPointAdapterChecks
{
    internal static int Run(string[] args)
    {
        if (args.Length is not (5 or 6) || args.Length == 6 && args[5] != "read-only")
        { Console.Error.WriteLine("powerpoint-checks APP_EXE HWND SYNTHETIC_PPTX OUTPUT_JSON [read-only]"); return 2; }
        var requireReadOnly = args.Length == 6;
        var evidence = new List<object>();
        var diagnostics = new List<object>();
        var started = DateTimeOffset.UtcNow;
        string? beforeHash = null, afterHash = null, failure = null;
        string output = Path.GetFullPath(args[4]);
        if (File.Exists(output)) { Console.Error.WriteLine("Choose a new evidence output file; prior results are never overwritten."); return 2; }
        try
        {
            var app = Path.GetFullPath(args[1]);
            var hwnd = (nint)long.Parse(args[2]);
            var fixture = Path.GetFullPath(args[3]);
            var normalized = PathPolicy.Normalize(fixture);
            // This test may activate a window and navigate slides, so its document must be an explicit generated fixture.
            if (!fixture.Replace('/', '\\').Contains(@"\testdata\generated\", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(fixture), "workbookmark-powerpoint-fixture.pptx", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only testdata/generated/.../workbookmark-powerpoint-fixture.pptx is allowed.");
            if (!File.Exists(app) || !Native.IsWindow(hwnd) || Native.Class(hwnd) != "PPTFrameClass")
                throw new ArgumentException("Executable or PowerPoint editing window is unavailable.");
            beforeHash = Hash(fixture);
            var slideIds = ReadSlideIds(fixture);
            if (slideIds.Count < 3 || slideIds.Distinct().Count() != slideIds.Count) throw new InvalidDataException("Fixture requires at least three uniquely identified slides.");
            using var scope = new ComScope();
            var nativeWindows = new Dictionary<nint, object>();
            var nativePanes = Native.Children(hwnd, "paneClassDC", true);
            var candidateClass = "paneClassDC";
            if (nativePanes.Count == 0)
            {
                candidateClass = "mdiClass";
                nativePanes = Native.Children(hwnd, candidateClass, true);
            }
            diagnostics.Add(new { stage = "native-panes", rootHwnd = hwnd.ToInt64(), candidateClass, visiblePaneCount = nativePanes.Count });
            foreach (var pane in nativePanes)
            {
                var dispatch = new Guid("00020400-0000-0000-C000-000000000046");
                System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(Native.AccessibleObjectFromWindow(pane, unchecked((uint)-16), ref dispatch, out var window));
                scope.Keep(window); nativeWindows[ComScope.Identity(window)] = window;
            }
            diagnostics.Add(new { stage = "native-windows", connectedWindowCount = nativeWindows.Count });
            if (nativeWindows.Count == 0) throw new InvalidOperationException("No visible paneClassDC or compatible mdiClass document pane is available.");
            if (nativeWindows.Count > 1) throw new InvalidOperationException("Visible native panes identify different document windows.");
            var chosen = nativeWindows.Values.Single();
            var presentation = scope.Get(chosen, "Presentation");
            if (PathPolicy.Normalize(scope.Text(presentation, "FullName")) != normalized || !scope.Flag(presentation, "Saved"))
                throw new InvalidOperationException("Window is not the exact unchanged synthetic fixture; no navigation performed.");
            var readOnly = scope.Flag(presentation, "ReadOnly");
            diagnostics.Add(new { stage = "presentation-mode", requireReadOnly, readOnly });
            if (requireReadOnly && !readOnly) throw new InvalidOperationException("Fixture must already be open read-only; no navigation performed.");
            var view = scope.Get(chosen, "View");
            if (scope.Number(chosen, "ViewType") is not (1 or 9)) throw new InvalidOperationException("Fixture must already use normal or slide editing view.");
            if (!Native.SetForegroundWindow(hwnd)) throw new InvalidOperationException("Test foreground activation denied.");
            scope.Call(chosen, "Activate"); scope.Call(view, "GotoSlide", 2);
            Application.DoEvents(); Thread.Sleep(120);
            if (Native.GetForegroundWindow() != hwnd) throw new InvalidOperationException("Test foreground changed before capture.");
            var snapshot = ForegroundSnapshot.Capture();
            using var client = new WorkerClient(app);
            var capture = client.RunAsync(new(1, Guid.NewGuid(), Operation.Capture, DateTimeOffset.UtcNow.AddSeconds(3), snapshot)).GetAwaiter().GetResult();
            Check(capture.Code == ResultCode.Captured && capture.Target is { } captured && captured.Kind == TargetKind.PowerPointSlide &&
                PathPolicy.Normalize(captured.Path) == normalized && captured.SlideId == slideIds[1] &&
                captured.SlideNumber == 2 && captured.HadUnsavedChanges == false,
                "capture matches independent Open XML slide ID oracle", capture);
            scope.Call(view, "GotoSlide", 1); Application.DoEvents();
            var resume = client.RunAsync(new(1, Guid.NewGuid(), Operation.Resume, DateTimeOffset.UtcNow.AddSeconds(15), ForegroundSnapshot.Capture(), capture.Target)).GetAwaiter().GetResult();
            Check(resume.Code is ResultCode.PositionRestored or ResultCode.PositionRestoredFocusPending &&
                scope.Number(scope.Get(view, "Slide"), "SlideID") == slideIds[1], "resume restores captured slide after deliberate navigation away", resume);
            var missingId = int.MaxValue;
            while (slideIds.Contains(missingId)) missingId--;
            var missing = capture.Target! with { SlideId = missingId };
            var rejected = client.RunAsync(new(1, Guid.NewGuid(), Operation.Resume, DateTimeOffset.UtcNow.AddSeconds(15), ForegroundSnapshot.Capture(), missing)).GetAwaiter().GetResult();
            Check(rejected.Code == ResultCode.OpenedPositionFailed && !rejected.ExternalActionStarted &&
                scope.Number(scope.Get(view, "Slide"), "SlideID") == slideIds[1], "missing slide ID refuses navigation without external action", rejected);
            afterHash = Hash(fixture);
            Check(beforeHash == afterHash && scope.Flag(presentation, "Saved") && scope.Flag(presentation, "ReadOnly") == readOnly,
                "fixture bytes, Saved state and read-only state remain unchanged", null);
        }
        catch (Exception error) { failure = error.GetType().Name + ": " + error.Message; }
        finally
        {
            try { afterHash ??= Hash(Path.GetFullPath(args[3])); } catch { }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(new { test = "PowerPoint synthetic capture and resume", requireReadOnly, startedUtc = started,
                finishedUtc = DateTimeOffset.UtcNow, beforeSha256 = beforeHash, afterSha256 = afterHash,
                passed = failure is null && beforeHash is not null && beforeHash == afterHash, failure, checks = evidence, diagnostics,
                limitations = "One native editing window and saved .pptx. Does not establish multiple windows, reordered/deleted real slides, .ppt/.pptm, Office 32-bit, protected view, or 50-capture gate. No Office document saved, closed or process terminated."
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

    private static List<int> ReadSlideIds(string fixture)
    {
        using var stream = new FileStream(fixture, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using var content = (zip.GetEntry("ppt/presentation.xml") ?? throw new InvalidDataException("Presentation XML missing.")).Open();
        XNamespace p = "http://schemas.openxmlformats.org/presentationml/2006/main";
        return XDocument.Load(content).Root?.Element(p + "sldIdLst")?.Elements(p + "sldId").Select(slide => int.Parse(slide.Attribute("id")!.Value)).ToList() ?? [];
    }

    private static string Hash(string fixture)
    {
        using var stream = new FileStream(fixture, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
