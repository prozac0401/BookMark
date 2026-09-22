using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using WorkBookmark.Core;

namespace WorkBookmark.Windows;

/// <summary>Optional SumatraPDF 3.7 protocol adapter. Adobe/Edge never receive PDF position requests.</summary>
internal static class PdfAdapter
{
    internal const string FrameClass = "SUMATRA_PDF_FRAME";
    internal sealed record FileState(string Path, int Page, int PageCount, Version Version);
    private sealed record Viewer(nint Hwnd, uint ProcessId, long Stamp, string Executable);
    private sealed record OpenViewer(Viewer Viewer, IReadOnlyList<string> Files);

    internal static CapturedTarget Capture(TargetSnapshot snapshot, RequestContext context)
    {
        ForegroundSnapshot.Verify(snapshot);
        var viewer = InspectProcess(snapshot.ProcessId);
        if (viewer.Hwnd.ToInt64() != snapshot.Hwnd || viewer.Stamp != snapshot.ProcessStartTimeUtcTicks)
            throw new BookmarkException(ResultCode.ContextChanged);
        using var dde = new PdfDdeClient(viewer.Hwnd, context);
        VerifyViewer(viewer); context.Check(); ForegroundSnapshot.Verify(snapshot);
        var first = ParseState(dde.Request("[GetFileState()]"));
        VerifyViewer(viewer); context.Check(); ForegroundSnapshot.Verify(snapshot);
        var second = ParseState(dde.Request("[GetFileState()]"));
        if (first != second) throw new BookmarkException(ResultCode.ContextChanged);
        var target = PathPolicy.Validate(new CapturedTarget(TargetKind.PdfPage, first.Path, PdfPage: first.Page));
        WindowsAdapter.CheckExists(target);
        VerifyViewer(viewer); context.Check(); ForegroundSnapshot.Verify(snapshot);
        return target;
    }

    internal static WorkerResponse Resume(CapturedTarget target, RequestContext context, bool validateOnly)
    {
        PathPolicy.Validate(target);
        if (target.Kind != TargetKind.PdfPage || target.PdfPage is not > 0) throw new BookmarkException(ResultCode.UnsupportedTarget);
        using var guard = new ResumeGuard(context.Request.Snapshot,
            context.Request.MonitorInput ? context.Request.RequestId : null);
        var path = PathPolicy.Normalize(target.Path);
        var opened = false;
        while (true)
        {
            context.Check();
            IReadOnlyList<OpenViewer> inventory;
            try { inventory = Inventory(context); }
            catch (BookmarkException error) when (opened && error.Code is ResultCode.EnumerationIncomplete or ResultCode.UnsupportedTarget)
            {
                // A newly launched process can expose its frame before DDE/document loading is ready.
                // Observe the one prior request; never issue another open during startup uncertainty.
                context.Check(); System.Windows.Forms.Application.DoEvents(); Thread.Sleep(100); continue;
            }
            var matches = inventory.SelectMany(v => v.Files.Where(p => string.Equals(p, path, StringComparison.Ordinal)).Select(_ => v.Viewer)).ToList();
            if (matches.Count > 1) throw new BookmarkException(ResultCode.AmbiguousTarget);
            // Case/physical aliases cannot silently look like a closed document.
            foreach (var candidate in inventory.SelectMany(v => v.Files))
            {
                if (candidate == path) continue;
                if (string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase) ||
                    FileIdentity.TryRead(path) is { } identity && FileIdentity.TryRead(candidate) == identity)
                    throw new BookmarkException(ResultCode.AmbiguousTarget);
            }
            if (matches.Count == 1)
            {
                var viewer = matches[0];
                guard.Permit(viewer.Hwnd); context.TargetHwnd = viewer.Hwnd.ToInt64();
                context.DocumentObserved = true;
                using var dde = new PdfDdeClient(viewer.Hwnd, context);
                VerifyViewer(viewer); context.Check();
                guard.Check();
                // GotoPage binds its file argument to the existing document. Acknowledgement alone
                // is insufficient: re-read the visible document's path and 1-based page afterwards.
                if (validateOnly)
                {
                    // Do not activate another tab just to validate a relink.
                    var current = ParseState(dde.Request("[GetFileState()]"));
                    VerifyViewer(viewer); guard.Check(); context.Check();
                    if (current.Path != path || target.PdfPage > current.PageCount) throw new BookmarkException(ResultCode.TargetUnavailable);
                    return context.Response(ResultCode.Validated, target);
                }
                context.ExternalActionStarted = true;
                dde.Execute(PageCommand(path, target.PdfPage.Value));
                var actual = ParseState(dde.Request("[GetFileState()]"));
                VerifyViewer(viewer); context.Check();
                if (!Matches(actual, target)) return context.Response(ResultCode.OpenedPositionFailed);
                context.PositionVerified = true;
                guard.Check();
                if (Native.IsIconic(viewer.Hwnd)) Native.ShowWindowAsync(viewer.Hwnd, 9);
                guard.Check(); context.Check();
                var focused = Native.SetForegroundWindow(viewer.Hwnd) && Native.GetForegroundWindow() == viewer.Hwnd;
                return context.Response(focused ? ResultCode.PositionRestored : ResultCode.PositionRestoredFocusPending);
            }
            if (validateOnly) throw new BookmarkException(ResultCode.TargetUnavailable);
            if (!opened)
            {
                WindowsAdapter.CheckExists(target);
                if (inventory.Count > 1) throw new BookmarkException(ResultCode.AmbiguousTarget);
                if (inventory.Count == 1)
                {
                    var viewer = inventory[0].Viewer;
                    using var dde = new PdfDdeClient(viewer.Hwnd, context);
                    guard.Permit(viewer.Hwnd); VerifyViewer(viewer); context.Check();
                    context.ExternalActionStarted = true;
                    dde.Execute(OpenCommand(path));
                }
                else
                {
                    var executable = FindRegisteredExecutable() ?? throw new BookmarkException(ResultCode.UnsupportedTarget);
                    context.Check();
                    // Launch this verified registered viewer directly; the default PDF app is irrelevant.
                    var start = new ProcessStartInfo(executable) { UseShellExecute = false };
                    start.ArgumentList.Add(path);
                    context.ExternalActionStarted = true;
                    using var process = Process.Start(start);
                    if (process is null) throw new BookmarkException(ResultCode.ResumeOutcomeUnknown);
                }
                opened = true;
            }
            System.Windows.Forms.Application.DoEvents(); Thread.Sleep(100);
        }
    }

    private static IReadOnlyList<OpenViewer> Inventory(RequestContext context)
    {
        var before = ProcessIds();
        var result = new List<OpenViewer>();
        foreach (var pid in before)
        {
            context.Check(); var viewer = InspectProcess(pid);
            using var dde = new PdfDdeClient(viewer.Hwnd, context);
            var files = ParseOpenFiles(dde.Request("[GetOpenFiles()]"));
            VerifyViewer(viewer); context.Check();
            result.Add(new(viewer, files));
        }
        if (!before.SetEquals(ProcessIds())) throw new BookmarkException(ResultCode.EnumerationIncomplete);
        return result;
    }
    private static HashSet<uint> ProcessIds()
    {
        using var current = Process.GetCurrentProcess();
        var result = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName("SumatraPDF"))
        {
            using (process)
            {
                try { if (process.SessionId == current.SessionId) result.Add((uint)process.Id); }
                catch { throw new BookmarkException(ResultCode.EnumerationIncomplete); }
            }
        }
        return result;
    }
    private static Viewer InspectProcess(uint pid)
    {
        if (!Native.IsUnelevatedProcess(pid)) throw new BookmarkException(ResultCode.UnsupportedTarget);
        var roots = new List<nint>();
        Native.EnumWindows((hwnd, _) =>
        {
            Native.GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid && Native.Class(hwnd) == FrameClass) roots.Add(hwnd);
            return true;
        }, 0);
        // GetFileState ignores the receiving HWND and uses the process's last active frame.
        // With exactly one frame that fallback cannot name a different window.
        if (roots.Count != 1) throw new BookmarkException(roots.Count == 0 ? ResultCode.EnumerationIncomplete : ResultCode.AmbiguousTarget);
        using var process = Process.GetProcessById(checked((int)pid));
        var executable = process.MainModule?.FileName;
        if (!ValidExecutable(executable)) throw new BookmarkException(ResultCode.UnsupportedTarget);
        var stamp = Native.ProcessStamp(pid);
        if (stamp == 0) throw new BookmarkException(ResultCode.EnumerationIncomplete);
        return new(roots[0], pid, stamp, executable!);
    }
    private static void VerifyViewer(Viewer viewer)
    {
        if (InspectProcess(viewer.ProcessId) != viewer) throw new BookmarkException(ResultCode.ContextChanged);
    }
    private static bool ValidExecutable(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Path.GetFileName(path).Equals("SumatraPDF.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
        var info = FileVersionInfo.GetVersionInfo(path);
        return info.ProductName == "SumatraPDF" && (info.FileMajorPart > 3 || info.FileMajorPart == 3 && info.FileMinorPart >= 7);
    }
    private static string? FindRegisteredExecutable()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\SumatraPDF.exe");
            if (key?.GetValue(null) is not string path || !ValidExecutable(path)) continue;
            candidates.Add(path);
        }
        if (candidates.Count > 1) throw new BookmarkException(ResultCode.AmbiguousTarget);
        return candidates.SingleOrDefault();
    }

    internal static FileState ParseState(string response)
    {
        if (response.Length > 65536) throw new BookmarkException(ResultCode.UnsupportedTarget);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || !fields.TryAdd(line[..colon], line[(colon + 1)..].Trim(' ', '\r')))
                throw new BookmarkException(ResultCode.UnsupportedTarget);
        }
        if (fields.ContainsKey("error") || !fields.TryGetValue("path", out var path) || !fields.TryGetValue("page", out var page) ||
            !fields.TryGetValue("pageCount", out var count) || !fields.TryGetValue("sumver", out var version) ||
            !int.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out var pageNumber) ||
            !int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var pageCount) ||
            pageNumber <= 0 || pageNumber > pageCount || !Version.TryParse(version, out var parsedVersion) || parsedVersion < new Version(3, 7))
            throw new BookmarkException(ResultCode.UnsupportedTarget);
        var normalized = PathPolicy.Normalize(path);
        if (!Path.GetExtension(normalized).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) throw new BookmarkException(ResultCode.UnsupportedTarget);
        return new(normalized, pageNumber, pageCount, parsedVersion);
    }
    internal static IReadOnlyList<string> ParseOpenFiles(string response)
    {
        if (response.Length > 65536 || response.Contains('\0')) throw new BookmarkException(ResultCode.UnsupportedTarget);
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 1000) throw new BookmarkException(ResultCode.UnsupportedTarget);
        return lines.Select(line => PathPolicy.Normalize(line.TrimEnd('\r'))).ToArray();
    }
    internal static bool Matches(FileState actual, CapturedTarget target) =>
        actual.Path == PathPolicy.Normalize(target.Path) && actual.Page == target.PdfPage;
    internal static string PageCommand(string path, int page)
    {
        if (page <= 0) throw new BookmarkException(ResultCode.UnsupportedTarget);
        return "[GotoPage(" + QuotePath(path) + "," + page.ToString(CultureInfo.InvariantCulture) + ")]";
    }
    internal static string OpenCommand(string path) => "[Open(" + QuotePath(path) + ",0,0,0)]";
    private static string QuotePath(string path)
    {
        var normalized = PathPolicy.Normalize(path);
        if (!Path.GetExtension(normalized).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) throw new BookmarkException(ResultCode.UnsupportedTarget);
        return "\"" + normalized + "\"";
    }
}
