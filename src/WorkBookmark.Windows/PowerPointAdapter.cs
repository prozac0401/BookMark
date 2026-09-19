using System.Diagnostics;
using System.Runtime.InteropServices;
using WorkBookmark.Core;

namespace WorkBookmark.Windows;

/// <summary>PowerPoint editing-window navigation. Never reads slide text or changes presentation content.</summary>
internal static class PowerPointAdapter
{
    internal static Action<string>? ProbeTrace { get; set; }
    private static readonly Guid Dispatch = new("00020400-0000-0000-C000-000000000046");
    private sealed record ConnectedWindow(object Window, object Application, nint Hwnd, nint Pane, uint Pid, long Stamp);
    private sealed record Position(CapturedTarget Target, object Presentation, object Slide);
    private sealed record Match(object Presentation, object Application, IReadOnlyList<ConnectedWindow> Windows);
    private sealed record Enumeration(IReadOnlyList<Match> Matches, bool Complete);

    internal static CapturedTarget Capture(TargetSnapshot snapshot, RequestContext context)
    {
        ForegroundSnapshot.Verify(snapshot);
        using var scope = new ComScope();
        // Normal view can contain multiple paneClassDC panes for one DocumentWindow.
        // Every visible native pane must identify the same window; never choose the first one.
        var connection = Connect((nint)snapshot.Hwnd, scope, visibleOnly: true);
        if (connection.Pid != snapshot.ProcessId || connection.Stamp != snapshot.ProcessStartTimeUtcTicks)
            throw new BookmarkException(ResultCode.ContextChanged);
        context.Check(); ForegroundSnapshot.Verify(snapshot);
        var first = ReadPosition(connection, scope, requireActive: true);
        context.Check(); ForegroundSnapshot.Verify(snapshot);
        var second = ReadPosition(connection, scope, requireActive: true);
        if (first.Target != second.Target || !ComScope.Same(first.Presentation, second.Presentation) || !ComScope.Same(first.Slide, second.Slide))
            throw new BookmarkException(ResultCode.ContextChanged);
        VerifyConnection(connection, scope);
        WindowsAdapter.CheckExists(first.Target);
        context.Check(); ForegroundSnapshot.Verify(snapshot);
        return first.Target;
    }

    private static ConnectedWindow Connect(nint root, ComScope scope, bool visibleOnly)
    {
        if (!Native.IsWindow(root) || Native.Class(root) != "PPTFrameClass") throw new BookmarkException(ResultCode.UnsupportedTarget);
        Native.GetWindowThreadProcessId(root, out var pid);
        var stamp = Native.ProcessStamp(pid);
        if (stamp == 0 || !Native.IsUnelevatedProcess(pid)) throw new BookmarkException(ResultCode.UnsupportedTarget);
        var connected = new Dictionary<nint, ConnectedWindow>();
        foreach (var child in NativePanes(root, visibleOnly))
        {
            Native.GetWindowThreadProcessId(child, out var childPid);
            if (!Native.BelongsTo(child, root) || childPid != pid) throw new BookmarkException(ResultCode.ContextChanged);
            var dispatch = Dispatch;
            Marshal.ThrowExceptionForHR(Native.AccessibleObjectFromWindow(child, unchecked((uint)-16), ref dispatch, out var window));
            scope.Keep(window);
            var application = scope.Get(window, "Application");
            connected[ComScope.Identity(window)] = new(window, application, root, child, pid, stamp);
        }
        if (connected.Count != 1) throw new BookmarkException(connected.Count == 0 ? ResultCode.EnumerationIncomplete : ResultCode.AmbiguousTarget);
        var result = connected.Values.Single();
        VerifyConnection(result, scope);
        return result;
    }

    private static void VerifyConnection(ConnectedWindow connection, ComScope scope)
    {
        Native.GetWindowThreadProcessId(connection.Hwnd, out var rootPid);
        Native.GetWindowThreadProcessId(connection.Pane, out var panePid);
        if (!Native.IsWindow(connection.Hwnd) || !Native.IsWindow(connection.Pane) ||
            !Native.BelongsTo(connection.Pane, connection.Hwnd) || Native.Class(connection.Hwnd) != "PPTFrameClass" ||
            Native.Class(connection.Pane) is not ("paneClassDC" or "mdiClass") || rootPid != connection.Pid || panePid != connection.Pid ||
            Native.ProcessStamp(rootPid) != connection.Stamp)
            throw new BookmarkException(ResultCode.ContextChanged);
        var dispatch = Dispatch;
        Marshal.ThrowExceptionForHR(Native.AccessibleObjectFromWindow(connection.Pane, unchecked((uint)-16), ref dispatch, out var window));
        scope.Keep(window);
        if (!ComScope.Same(window, connection.Window) || !ComScope.Same(scope.Get(window, "Application"), connection.Application))
            throw new BookmarkException(ResultCode.ContextChanged);
    }

    private static Position ReadPosition(ConnectedWindow connection, ComScope scope, bool requireActive)
    {
        VerifyConnection(connection, scope);
        if (requireActive && !ComScope.Same(scope.Get(connection.Application, "ActiveWindow"), connection.Window))
            throw new BookmarkException(ResultCode.ContextChanged);
        RequireEditingView(connection.Window, scope);
        var presentation = scope.Get(connection.Window, "Presentation");
        if (!ComScope.Same(scope.Get(presentation, "Application"), connection.Application) ||
            (requireActive && !ComScope.Same(scope.Get(connection.Application, "ActivePresentation"), presentation)))
            throw new BookmarkException(ResultCode.ContextChanged);
        // PowerPoint collection Item is a method; request DISPATCH_METHOD for every collection lookup.
        var windows = scope.Get(presentation, "Windows");
        var count = scope.Number(windows, "Count"); var member = false;
        for (var index = 1; index <= count; index++)
            if (ComScope.Same(scope.Call(windows, "Item", index), connection.Window)) member = true;
        if (!member || scope.Number(windows, "Count") != count) throw new BookmarkException(ResultCode.ContextChanged);
        // Multiple thumbnail selections do not describe a single captured slide.
        var selection = scope.Get(connection.Window, "Selection");
        if (scope.Number(selection, "Type") == 1 && scope.Number(scope.Get(selection, "SlideRange"), "Count") != 1)
            throw new BookmarkException(ResultCode.MultipleSelection);
        var slide = scope.Get(scope.Get(connection.Window, "View"), "Slide");
        if (!ComScope.Same(scope.Get(slide, "Parent"), presentation)) throw new BookmarkException(ResultCode.UnsupportedTarget);
        var slideId = scope.Number(slide, "SlideID");
        var slideNumber = scope.Number(slide, "SlideIndex");
        var slides = scope.Get(presentation, "Slides");
        if (slideId <= 0 || slideNumber <= 0 || !ComScope.Same(scope.Call(slides, "Item", slideNumber), slide))
            throw new BookmarkException(ResultCode.ContextChanged);
        if (string.IsNullOrWhiteSpace(scope.Text(presentation, "Path"))) throw new BookmarkException(ResultCode.UnsavedDocument);
        var target = new CapturedTarget(TargetKind.PowerPointSlide, scope.Text(presentation, "FullName"),
            HadUnsavedChanges: !scope.Flag(presentation, "Saved"), SlideId: slideId, SlideNumber: slideNumber);
        PathPolicy.Validate(target);
        RequireLocalPath(target.Path);
        return new(target, presentation, slide);
    }

    private static void RequireEditingView(object window, ComScope scope)
    {
        // ppViewSlide / ppViewNormal. No master, sorter, print-preview or slide-show navigation.
        if (scope.Number(window, "ViewType") is not (1 or 9)) throw new BookmarkException(ResultCode.UnsupportedTarget);
    }

    internal static WorkerResponse Resume(CapturedTarget target, RequestContext context, bool validateOnly)
    {
        using var guard = new ResumeGuard(context.Request.Snapshot);
        var normalized = RequireLocalPath(target.Path);
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
                // Observe only the one Shell request while PowerPoint initializes its native panes.
                System.Windows.Forms.Application.DoEvents(); Thread.Sleep(125); continue;
            }
            if (enumeration.Matches.Count == 1)
            {
                var match = enumeration.Matches[0];
                foreach (var window in match.Windows) guard.Permit(window.Hwnd);
                var selected = ChooseWindow(match, scope);
                context.TargetHwnd = selected.Hwnd.ToInt64();
                var reconnected = Connect(selected.Hwnd, scope, visibleOnly: false);
                if (!ComScope.Same(reconnected.Window, selected.Window) || !ComScope.Same(reconnected.Application, match.Application))
                    throw new BookmarkException(ResultCode.ContextChanged);
                try
                {
                    RequireEditingView(selected.Window, scope);
                    ValidateSlide(target, match.Presentation, scope, context);
                    if (validateOnly) return context.Response(ResultCode.Validated, target);
                    guard.Check(); context.Check();
                    context.ExternalActionStarted = true;
                    scope.Call(selected.Window, "Activate");
                    guard.Check(); context.Check();
                    VerifyConnection(selected, scope);
                    RequireEditingView(selected.Window, scope);
                    if (!ComScope.Same(scope.Get(selected.Window, "Presentation"), match.Presentation))
                        throw new BookmarkException(ResultCode.ContextChanged);
                    // Resolve stable SlideID immediately before moving: slide order may have changed.
                    var slide = ValidateSlide(target, match.Presentation, scope, context);
                    var currentIndex = scope.Number(slide, "SlideIndex");
                    var view = scope.Get(selected.Window, "View");
                    guard.Check(); context.Check();
                    scope.Call(view, "GotoSlide", currentIndex);
                    context.Check();
                    var actual = ReadPosition(selected, scope, requireActive: true);
                    if (!string.Equals(PathPolicy.Normalize(actual.Target.Path), normalized, StringComparison.Ordinal) || actual.Target.SlideId != target.SlideId)
                        return context.Response(ResultCode.OpenedPositionFailed);
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
                if (Type.GetTypeFromProgID("PowerPoint.Application") is null) throw new BookmarkException(ResultCode.UnsupportedTarget);
                guard.Check(); context.Check();
                WindowsAdapter.ShellOpen(target.Path, context);
                opened = true;
            }
            System.Windows.Forms.Application.DoEvents(); Thread.Sleep(125);
        }
    }

    private static object ValidateSlide(CapturedTarget target, object presentation, ComScope scope, RequestContext context)
    {
        context.Check();
        if (!string.Equals(PathPolicy.Normalize(scope.Text(presentation, "FullName")), PathPolicy.Normalize(target.Path), StringComparison.Ordinal))
            throw new BookmarkException(ResultCode.ContextChanged);
        var slides = scope.Get(presentation, "Slides");
        var count = scope.Number(slides, "Count");
        object? found = null;
        for (var index = 1; index <= count; index++)
        {
            context.Check();
            var candidate = scope.Call(slides, "Item", index);
            if (scope.Number(candidate, "SlideID") != target.SlideId) continue;
            if (found is not null) throw new BookmarkException(ResultCode.AmbiguousTarget);
            found = candidate;
        }
        if (found is null) throw new BookmarkException(ResultCode.TargetUnavailable);
        var currentIndex = scope.Number(found, "SlideIndex");
        if (scope.Number(slides, "Count") != count || currentIndex < 1 || currentIndex > count ||
            !ComScope.Same(scope.Get(found, "Parent"), presentation) || !ComScope.Same(scope.Call(slides, "Item", currentIndex), found) ||
            scope.Number(found, "SlideID") != target.SlideId)
            throw new BookmarkException(ResultCode.ContextChanged);
        context.Check(); return found;
    }

    private static ConnectedWindow ChooseWindow(Match match, ComScope scope)
    {
        var windows = match.Windows.Where(w => Native.IsWindow(w.Hwnd) && Native.IsWindowVisible(w.Hwnd)).ToList();
        if (windows.Count == 0) throw new BookmarkException(ResultCode.TargetUnavailable);
        var active = scope.Get(match.Application, "ActiveWindow");
        return windows.FirstOrDefault(w => active is not null && ComScope.Same(w.Window, active)) ?? windows.OrderBy(w => w.Hwnd.ToInt64()).First();
    }

    private static Enumeration Enumerate(string targetPath, ComScope scope, RequestContext context)
    {
        var processesBefore = PowerPointProcesses();
        var roots = PowerPointRoots();
        var windows = new Dictionary<nint, ConnectedWindow>();
        var applications = new Dictionary<nint, object>();
        var connectedPids = new HashSet<uint>();
        var hiddenControlPids = new HashSet<uint>();
        var complete = true;
        void Incomplete(string reason) { complete = false; ProbeTrace?.Invoke(reason); }
        foreach (var root in roots)
        {
            context.Check(); Native.GetWindowThreadProcessId(root, out var pid);
            if (!Native.IsUnelevatedProcess(pid)) { Incomplete("process-elevation-or-access"); continue; }
            if (!Native.IsWindowVisible(root) && NativePanes(root, visibleOnly: false).Count == 0)
            {
                hiddenControlPids.Add(pid); continue;
            }
            try
            {
                var connected = Connect(root, scope, visibleOnly: false);
                var identity = ComScope.Identity(connected.Window);
                if (windows.TryGetValue(identity, out var previous) && previous.Hwnd != connected.Hwnd) { Incomplete("native-window-alias"); continue; }
                windows[identity] = connected;
                applications[ComScope.Identity(connected.Application)] = connected.Application;
                connectedPids.Add(pid);
            }
            catch (Exception exception) { Incomplete("connection:" + exception.GetType().Name + ":" + exception.HResult); }
        }
        if (processesBefore.Any(pid => !connectedPids.Contains(pid)) || hiddenControlPids.Any(pid => !connectedPids.Contains(pid))) Incomplete("unconnected-process");
        var matches = new Dictionary<nint, Match>();
        var targetIdentity = FileIdentity.TryRead(targetPath);
        foreach (var application in applications.Values)
        {
            context.Check();
            try
            {
                if (scope.Number(scope.Get(application, "ProtectedViewWindows"), "Count") != 0) Incomplete("protected-view");
                if (scope.Number(scope.Get(application, "SlideShowWindows"), "Count") != 0) Incomplete("slide-show-active");
                var presentations = scope.Get(application, "Presentations");
                var count = scope.Number(presentations, "Count");
                var identities = new List<nint>();
                for (var index = 1; index <= count; index++)
                {
                    context.Check(); var presentation = scope.Call(presentations, "Item", index);
                    identities.Add(ComScope.Identity(presentation));
                    if (string.IsNullOrEmpty(scope.Text(presentation, "Path"))) continue;
                    var fullName = scope.Text(presentation, "FullName");
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
                    if (possibleAlias && !exact) { Incomplete("path-alias"); continue; }
                    if (!exact) continue;
                    var documentWindows = scope.Get(presentation, "Windows");
                    var windowCount = scope.Number(documentWindows, "Count");
                    var windowIdentities = new List<nint>();
                    var verified = new List<ConnectedWindow>();
                    for (var windowIndex = 1; windowIndex <= windowCount; windowIndex++)
                    {
                        context.Check(); var window = scope.Call(documentWindows, "Item", windowIndex);
                        var identity = ComScope.Identity(window); windowIdentities.Add(identity);
                        if (!windows.TryGetValue(identity, out var native) || !ComScope.Same(native.Application, application) ||
                            !ComScope.Same(scope.Get(window, "Presentation"), presentation)) { Incomplete("window-owner"); continue; }
                        VerifyConnection(native, scope); verified.Add(native);
                    }
                    if (scope.Number(documentWindows, "Count") != windowCount || verified.Count == 0 || verified.Count != windowCount) Incomplete("presentation-window-inventory");
                    else for (var windowIndex = 1; windowIndex <= windowCount; windowIndex++)
                        if (ComScope.Identity(scope.Call(documentWindows, "Item", windowIndex)) != windowIdentities[windowIndex - 1]) Incomplete("window-identity-changed");
                    matches[ComScope.Identity(presentation)] = new(presentation, application, verified);
                }
                if (scope.Number(presentations, "Count") != count) Incomplete("presentation-count-changed");
                else for (var index = 1; index <= count; index++)
                    if (ComScope.Identity(scope.Call(presentations, "Item", index)) != identities[index - 1]) Incomplete("presentation-identity-changed");
            }
            catch (BookmarkException) { throw; }
            catch (Exception exception) { Incomplete("presentation-enumeration:" + exception.GetType().Name + ":" + exception.HResult); }
        }
        if (!processesBefore.SetEquals(PowerPointProcesses()) || !roots.ToHashSet().SetEquals(PowerPointRoots())) Incomplete("process-root-inventory-changed");
        context.Check(); return new(matches.Values.ToList(), complete);
    }

    private static List<nint> NativePanes(nint root, bool visibleOnly)
    {
        var documented = Native.Children(root, "paneClassDC", visibleOnly);
        if (documented.Count != 0) return documented;
        // The installed Office build exposes its DocumentWindow on mdiClass (observed 2026-09-19).
        // Use this one compatibility class only when the documented pane class is absent.
        // Every candidate still passes PID/stamp, NativeOM identity and document membership checks.
        return Native.Children(root, "mdiClass", visibleOnly);
    }

    private static string RequireLocalPath(string path)
    {
        var normalized = PathPolicy.Normalize(path);
        if (normalized.Length < 3 || normalized[1] != ':') throw new BookmarkException(ResultCode.UnsupportedTarget);
        return normalized;
    }

    private static List<nint> PowerPointRoots()
    {
        var roots = new List<nint>();
        Native.EnumWindows((hwnd, _) => { if (Native.Class(hwnd) == "PPTFrameClass") roots.Add(hwnd); return true; }, 0);
        return roots;
    }

    private static HashSet<uint> PowerPointProcesses()
    {
        var found = new HashSet<uint>();
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName("POWERPNT"))
        {
            using (process)
            {
                try { if (process.SessionId == current.SessionId) found.Add((uint)process.Id); }
                catch { throw new BookmarkException(ResultCode.EnumerationIncomplete); }
            }
        }
        return found;
    }
}
