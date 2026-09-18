using System.Diagnostics;
using System.Runtime.InteropServices;
using WorkBookmark.Core;

namespace WorkBookmark.Windows;

internal static class ExcelAdapter
{
    internal static Action<string>? ProbeTrace { get; set; }
    private static readonly Guid Dispatch = new("00020400-0000-0000-C000-000000000046");
    private sealed record ConnectedWindow(object Window, object Application, nint Hwnd);
    private sealed record Position(CapturedTarget Target, object Workbook, object Worksheet, object Cell, object Application);
    private sealed record Match(object Workbook, object Application, IReadOnlyList<ConnectedWindow> Windows);
    private sealed record Enumeration(IReadOnlyList<Match> Matches, bool Complete);

    internal static CapturedTarget Capture(TargetSnapshot snapshot, RequestContext context)
    {
        ForegroundSnapshot.Verify(snapshot);
        if (snapshot.ActiveViewHwnd == 0) throw new BookmarkException(ResultCode.AmbiguousTarget);
        using var scope = new ComScope();
        var connection = Connect((nint)snapshot.Hwnd, scope, visibleOnly: true);
        context.Check(); ForegroundSnapshot.Verify(snapshot);
        var first = ReadPosition(connection, scope, requireActive: true);
        context.Check(); ForegroundSnapshot.Verify(snapshot);
        var second = ReadPosition(connection, scope, requireActive: true);
        ForegroundSnapshot.Verify(snapshot);
        if (first.Target != second.Target || !ComScope.Same(first.Workbook, second.Workbook) || !ComScope.Same(first.Worksheet, second.Worksheet))
            throw new BookmarkException(ResultCode.ContextChanged);
        WindowsAdapter.CheckExists(first.Target);
        context.Check(); ForegroundSnapshot.Verify(snapshot);
        return first.Target;
    }

    private static ConnectedWindow Connect(nint root, ComScope scope, bool visibleOnly)
    {
        var children = Native.Children(root, "EXCEL7", visibleOnly);
        var connected = new Dictionary<nint, ConnectedWindow>();
        foreach (var child in children)
        {
            var dispatch = Dispatch;
            Marshal.ThrowExceptionForHR(Native.AccessibleObjectFromWindow(child, unchecked((uint)-16), ref dispatch, out var window));
            scope.Keep(window);
            var hwnd = WindowHandle(scope.Get(window, "Hwnd"));
            if (!Native.IsWindow(hwnd) || !Native.BelongsTo(child, root) || !Native.BelongsTo(hwnd, root)) throw new BookmarkException(ResultCode.ContextChanged);
            var application = scope.Get(window, "Application");
            connected[ComScope.Identity(window)] = new(window, application, hwnd);
        }
        if (connected.Count != 1) throw new BookmarkException(connected.Count == 0 ? ResultCode.EnumerationIncomplete : ResultCode.AmbiguousTarget);
        return connected.Values.Single();
    }

    private static Position ReadPosition(ConnectedWindow connection, ComScope scope, bool requireActive)
    {
        if (!Native.IsWindow(connection.Hwnd) || WindowHandle(scope.Get(connection.Window, "Hwnd")) != connection.Hwnd)
            throw new BookmarkException(ResultCode.ContextChanged);
        if (requireActive)
        {
            var activeWindow = scope.Get(connection.Application, "ActiveWindow");
            if (activeWindow is null || !SameWindow(activeWindow, connection.Window, scope)) throw new BookmarkException(ResultCode.ContextChanged);
        }
        var sheet = scope.Get(connection.Window, "ActiveSheet");
        if (scope.Number(sheet, "Type") != -4167) throw new BookmarkException(ResultCode.UnsupportedTarget); // xlWorksheet
        var active = scope.Get(connection.Window, "ActiveCell");
        if (!ComScope.Same(scope.Get(active, "Parent"), sheet)) throw new BookmarkException(ResultCode.ContextChanged);
        var workbook = scope.Get(sheet, "Parent");
        var application = scope.Get(workbook, "Application");
        if (!ComScope.Same(application, connection.Application)) throw new BookmarkException(ResultCode.ContextChanged);
        if (requireActive && !ComScope.Same(scope.Get(application, "ActiveWorkbook"), workbook)) throw new BookmarkException(ResultCode.ContextChanged);
        var windows = scope.Get(workbook, "Windows");
        var count = scope.Number(windows, "Count"); var member = false;
        for (var i = 1; i <= count; i++)
        {
            var window = scope.Get(windows, "Item", i);
            if (SameWindow(window, connection.Window, scope)) member = true;
        }
        if (!member || scope.Number(windows, "Count") != count) throw new BookmarkException(ResultCode.ContextChanged);
        if (string.IsNullOrWhiteSpace(scope.Text(workbook, "Path"))) throw new BookmarkException(ResultCode.UnsavedWorkbook);
        var path = scope.Text(workbook, "FullName");
        var cell = CanonicalCell(active, scope);
        var address = scope.Text(cell, "Address", true, true, 1, false, Type.Missing);
        var target = new CapturedTarget(TargetKind.ExcelCell, path, scope.Text(sheet, "Name"), address, !scope.Flag(workbook, "Saved"));
        PathPolicy.Validate(target);
        return new(target, workbook, sheet, cell, application);
    }

    private static object CanonicalCell(object range, ComScope scope)
    {
        if (scope.Flag(range, "MergeCells"))
        {
            var area = scope.Get(range, "MergeArea");
            var cells = scope.Get(area, "Cells");
            return scope.Get(cells, "Item", 1, 1);
        }
        return range;
    }

    internal static WorkerResponse Resume(CapturedTarget target, RequestContext context, bool validateOnly)
    {
        using var guard = new ResumeGuard(context.Request.Snapshot);
        var normalized = PathPolicy.Normalize(target.Path);
        var opened = false;
        while (true)
        {
            context.Check();
            using var scope = new ComScope();
            var enumeration = Enumerate(normalized, scope, context);
            if (enumeration.Matches.Count > 1) throw new BookmarkException(ResultCode.AmbiguousTarget);
            if (!enumeration.Complete)
            {
                if (!opened)
                {
                    if (enumeration.Matches.Count == 0) WindowsAdapter.CheckExists(target);
                    throw new BookmarkException(ResultCode.EnumerationIncomplete);
                }
                // Startup may expose an XLMAIN before its native object model/workbook is ready.
                // Keep observing this one Shell request; incomplete enumeration never triggers another open.
                System.Windows.Forms.Application.DoEvents(); Thread.Sleep(125); continue;
            }
            if (enumeration.Matches.Count == 1)
            {
                var match = enumeration.Matches[0];
                foreach (var candidate in match.Windows) guard.Permit(candidate.Hwnd);
                var selected = ChooseWindow(match, scope);
                context.TargetHwnd = selected.Hwnd.ToInt64();
                var nativeWindow = Connect(selected.Hwnd, scope, visibleOnly: false);
                if (!SameWindow(nativeWindow.Window, selected.Window, scope) || !ComScope.Same(nativeWindow.Application, match.Application))
                    throw new BookmarkException(ResultCode.ContextChanged);
                try
                {
                    var range = ValidatePosition(target, match.Workbook, scope, context);
                    if (validateOnly) return context.Response(ResultCode.Validated, target);
                    guard.Check(); context.Check();
                    // Only activation and navigation are allowed external Excel actions.
                    context.ExternalActionStarted = true;
                    scope.Call(selected.Window, "Activate");
                    guard.Check(); context.Check();
                    var sheet = scope.Get(range, "Parent");
                    scope.Call(sheet, "Activate");
                    guard.Check(); context.Check();
                    scope.Call(match.Application, "Goto", range, true);
                    context.Check();
                    var actual = ReadPosition(selected, scope, requireActive: true);
                    if (!string.Equals(PathPolicy.Normalize(actual.Target.Path), normalized, StringComparison.Ordinal) ||
                        actual.Target.SheetName != target.SheetName || actual.Target.CellAddress != target.CellAddress)
                        return context.Response(ResultCode.OpenedPositionFailed);
                    // Foreground acquisition is separate from verified cell movement.
                    guard.Check(); context.Check();
                    if (!Native.IsWindow(selected.Hwnd)) return context.Response(ResultCode.PositionRestoredFocusPending);
                    if (Native.IsIconic(selected.Hwnd)) Native.ShowWindowAsync(selected.Hwnd, 9);
                    guard.Check(); context.Check();
                    var focused = Native.SetForegroundWindow(selected.Hwnd) && Native.GetForegroundWindow() == selected.Hwnd;
                    return context.Response(focused ? ResultCode.PositionRestored : ResultCode.PositionRestoredFocusPending);
                }
                catch (BookmarkException exception) when (exception.Code is ResultCode.UnsupportedTarget or ResultCode.TargetUnavailable)
                {
                    return context.Response(ResultCode.OpenedPositionFailed);
                }
            }
            if (!opened)
            {
                WindowsAdapter.CheckExists(target);
                if (Type.GetTypeFromProgID("Excel.Application") is null) throw new BookmarkException(ResultCode.UnsupportedTarget);
                guard.Check(); context.Check();
                WindowsAdapter.ShellOpen(target.Path, context);
                opened = true; // Never issue a second Shell open, even when observation fails.
            }
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(125);
        }
    }

    private static object ValidatePosition(CapturedTarget target, object workbook, ComScope scope, RequestContext context)
    {
        context.Check();
        if (!string.Equals(PathPolicy.Normalize(scope.Text(workbook, "FullName")), PathPolicy.Normalize(target.Path), StringComparison.Ordinal))
            throw new BookmarkException(ResultCode.ContextChanged);
        var worksheets = scope.Get(workbook, "Worksheets");
        var count = scope.Number(worksheets, "Count");
        object? sheet = null;
        for (var index = 1; index <= count; index++)
        {
            context.Check(); var candidate = scope.Get(worksheets, "Item", index);
            if (scope.Text(candidate, "Name") == target.SheetName) sheet = candidate;
        }
        if (sheet is null || scope.Number(sheet, "Visible") != -1 || scope.Number(worksheets, "Count") != count)
            throw new BookmarkException(ResultCode.TargetUnavailable);
        var range = scope.Get(sheet, "Range", target.CellAddress!, Type.Missing);
        var canonical = CanonicalCell(range, scope);
        if (scope.Text(canonical, "Address", true, true, 1, false, Type.Missing) != target.CellAddress)
            throw new BookmarkException(ResultCode.TargetUnavailable);
        var rows = scope.Get(range, "EntireRow"); var columns = scope.Get(range, "EntireColumn");
        if (scope.Flag(rows, "Hidden") || scope.Flag(columns, "Hidden")) throw new BookmarkException(ResultCode.TargetUnavailable);
        if (scope.Flag(sheet, "ProtectContents"))
        {
            var selection = scope.Number(sheet, "EnableSelection");
            if (selection == -4142 || (selection == 1 && scope.Flag(range, "Locked"))) throw new BookmarkException(ResultCode.TargetUnavailable);
        }
        context.Check(); return range;
    }

    private static ConnectedWindow ChooseWindow(Match match, ComScope scope)
    {
        var windows = match.Windows.Where(w => Native.IsWindow(w.Hwnd) && Native.IsWindowVisible(w.Hwnd)).ToList();
        if (windows.Count == 0) throw new BookmarkException(ResultCode.TargetUnavailable);
        var active = scope.Get(match.Application, "ActiveWindow");
        return windows.FirstOrDefault(w => active is not null && SameWindow(w.Window, active, scope)) ?? windows.OrderBy(w => w.Hwnd.ToInt64()).First();
    }

    private static Enumeration Enumerate(string targetPath, ComScope scope, RequestContext context)
    {
        var processesBefore = ExcelProcesses();
        var roots = Native.ExcelRoots();
        var connections = new Dictionary<nint, object>();
        var connectedPids = new HashSet<uint>();
        var hiddenControlPids = new HashSet<uint>();
        var complete = true;
        void Incomplete(string reason) { complete = false; ProbeTrace?.Invoke(reason); }
        foreach (var root in roots)
        {
            context.Check();
            Native.GetWindowThreadProcessId(root, out var pid);
            if (!Native.IsUnelevatedProcess(pid)) { Incomplete("process-elevation-or-access"); continue; }
            if (!Native.IsWindowVisible(root) && Native.Children(root, "EXCEL7", false).Count == 0)
            {
                // Excel also owns a hidden XLMAIN control window with no native document pane.
                // It is covered only when that same process has a connected Application whose
                // entire Workbooks/ProtectedViewWindows collections are enumerated below.
                hiddenControlPids.Add(pid); continue;
            }
            try
            {
                var connected = Connect(root, scope, visibleOnly: false);
                connections[ComScope.Identity(connected.Application)] = connected.Application;
                connectedPids.Add(pid);
            }
            catch (Exception ex) { Incomplete("connection:" + ex.GetType().Name + ":" + ex.HResult + ":root=" + root + ":pid=" + pid + ":visible=" + Native.IsWindowVisible(root) + ":children=" + Native.Children(root, "EXCEL7", false).Count); }
        }
        if (processesBefore.Any(pid => !connectedPids.Contains(pid)) || hiddenControlPids.Any(pid => !connectedPids.Contains(pid))) Incomplete("unconnected-process");
        var matches = new Dictionary<nint, Match>();
        var targetIdentity = FileIdentity.TryRead(targetPath);
        foreach (var application in connections.Values)
        {
            context.Check();
            try
            {
                var protectedViews = scope.Get(application, "ProtectedViewWindows");
                if (scope.Number(protectedViews, "Count") != 0) Incomplete("protected-view");
                var workbooks = scope.Get(application, "Workbooks");
                var count = scope.Number(workbooks, "Count");
                var identities = new List<nint>();
                for (var index = 1; index <= count; index++)
                {
                    context.Check();
                    var workbook = scope.Get(workbooks, "Item", index);
                    identities.Add(ComScope.Identity(workbook));
                    if (string.IsNullOrEmpty(scope.Text(workbook, "Path"))) continue;
                    var fullName = scope.Text(workbook, "FullName");
                    if (Uri.TryCreate(fullName, UriKind.Absolute, out var uri) && !uri.IsFile) continue;
                    var path = PathPolicy.Normalize(fullName);
                    var exact = string.Equals(path, targetPath, StringComparison.Ordinal);
                    var possibleAlias = string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase);
                    if (!exact)
                    {
                        var identity = FileIdentity.TryRead(path);
                        if (targetIdentity is null || identity is null) { Incomplete("file-identity-unavailable"); continue; }
                        possibleAlias |= identity == targetIdentity;
                    }
                    // Permanent keys are ordinal. Aliases are detected, never guessed as absence or merged silently.
                    if (possibleAlias && !exact) { Incomplete("path-alias"); continue; }
                    if (!exact) continue;
                    var windows = scope.Get(workbook, "Windows");
                    var windowCount = scope.Number(windows, "Count");
                    var verified = new List<ConnectedWindow>();
                    for (var windowIndex = 1; windowIndex <= windowCount; windowIndex++)
                    {
                        var window = scope.Get(windows, "Item", windowIndex);
                        var hwnd = WindowHandle(scope.Get(window, "Hwnd"));
                        Native.GetWindowThreadProcessId(hwnd, out var owner);
                        if (!Native.IsWindow(hwnd) || !connectedPids.Contains(owner) || !ComScope.Same(scope.Get(window, "Application"), application)) { Incomplete("window-owner"); continue; }
                        verified.Add(new(window, application, hwnd));
                    }
                    if (scope.Number(windows, "Count") != windowCount || verified.Count == 0) Incomplete("workbook-window-inventory");
                    matches[ComScope.Identity(workbook)] = new(workbook, application, verified);
                }
                if (scope.Number(workbooks, "Count") != count) Incomplete("workbook-count-changed");
                else for (var index = 1; index <= count; index++)
                    if (ComScope.Identity(scope.Get(workbooks, "Item", index)) != identities[index - 1]) Incomplete("workbook-identity-changed");
            }
            catch (BookmarkException) { throw; }
            catch (Exception ex) { Incomplete("workbook-enumeration:" + ex.GetType().Name + ":" + ex.HResult); }
        }
        if (!processesBefore.SetEquals(ExcelProcesses()) || !roots.ToHashSet().SetEquals(Native.ExcelRoots())) Incomplete("process-root-inventory-changed");
        context.Check();
        return new(matches.Values.ToList(), complete);
    }

    private static HashSet<uint> ExcelProcesses()
    {
        var found = new HashSet<uint>();
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName("EXCEL"))
        {
            using (process)
            {
                try { if (process.SessionId == current.SessionId) found.Add((uint)process.Id); }
                catch { throw new BookmarkException(ResultCode.EnumerationIncomplete); }
            }
        }
        return found;
    }
    private static bool SameWindow(object first, object second, ComScope scope)
    {
        // AccessibleObjectFromWindow and Application.Windows can expose different COM identities
        // for the same native Excel Window; verify its HWND and owning Application together.
        var hwnd = WindowHandle(scope.Get(first, "Hwnd"));
        return Native.IsWindow(hwnd) && hwnd == WindowHandle(scope.Get(second, "Hwnd")) &&
            ComScope.Same(scope.Get(first, "Application"), scope.Get(second, "Application"));
    }

    private static nint WindowHandle(object value)
    {
        var number = Convert.ToInt64(value);
        return number < 0 ? (nint)unchecked((uint)number) : (nint)number;
    }
}
