using System.Diagnostics;
using System.Runtime.InteropServices;
using WorkBookmark.Core;

namespace WorkBookmark.Windows;

/// <summary>Read-only document metadata plus explicit caret navigation. Never reads document text or saves/closes Word.</summary>
internal static class WordAdapter
{
    internal static Action<string>? ProbeTrace { get; set; }
    private static readonly Guid Dispatch = new("00020400-0000-0000-C000-000000000046");
    private sealed record ConnectedWindow(object Window, object Application, nint Hwnd, uint ProcessId, long ProcessStamp);
    private sealed record Position(CapturedTarget Target, object Document, int SelectionEnd, int SelectionType, int DocumentEnd);
    private sealed record Match(object Document, object Application, IReadOnlyList<ConnectedWindow> Windows);
    private sealed record Enumeration(IReadOnlyList<Match> Matches, bool Complete);

    internal static CapturedTarget Capture(TargetSnapshot snapshot, RequestContext context)
    {
        ForegroundSnapshot.Verify(snapshot);
        if (snapshot.ActiveViewHwnd == 0) throw new BookmarkException(ResultCode.AmbiguousTarget);
        using var scope = new ComScope();
        var connection = Connect((nint)snapshot.Hwnd, scope, context, visibleOnly: true);
        context.Check(); ForegroundSnapshot.Verify(snapshot);
        var first = ReadPosition(connection, scope, context, requireActive: true);
        context.Check(); ForegroundSnapshot.Verify(snapshot);
        var second = ReadPosition(connection, scope, context, requireActive: true);
        ForegroundSnapshot.Verify(snapshot);
        if (first.Target != second.Target || first.SelectionEnd != second.SelectionEnd || first.SelectionType != second.SelectionType ||
            first.DocumentEnd != second.DocumentEnd || !ComScope.Same(first.Document, second.Document))
            throw new BookmarkException(ResultCode.ContextChanged);
        WindowsAdapter.CheckExists(first.Target);
        context.Check(); ForegroundSnapshot.Verify(snapshot);
        return first.Target;
    }

    private static ConnectedWindow Connect(nint root, ComScope scope, RequestContext context, bool visibleOnly)
    {
        if (!Native.IsWindow(root) || Native.Class(root) != "OpusApp") throw new BookmarkException(ResultCode.ContextChanged);
        Native.GetWindowThreadProcessId(root, out var pid);
        var stamp = Native.ProcessStamp(pid);
        if (stamp == 0 || !Native.IsUnelevatedProcess(pid)) throw new BookmarkException(ResultCode.UnsupportedTarget);
        var connected = new Dictionary<nint, ConnectedWindow>();
        foreach (var child in Native.Children(root, "_WwG", visibleOnly))
        {
            context.Check();
            Native.GetWindowThreadProcessId(child, out var childPid);
            if (childPid != pid) throw new BookmarkException(ResultCode.ContextChanged);
            var dispatch = Dispatch;
            var result = Native.AccessibleObjectFromWindow(child, unchecked((uint)-16), ref dispatch, out var window);
            // Word keeps hidden _WwG implementation panes without a native object model.
            // They are not document-window candidates. A verified connection is still required
            // for this root, and the complete Documents/Windows inventory is checked below.
            if (result < 0 && !visibleOnly && !Native.IsWindowVisible(child)) continue;
            Marshal.ThrowExceptionForHR(result);
            scope.Keep(window);
            var hwnd = WindowHandle(scope.Get(window, "Hwnd"));
            if (hwnd != root || !Native.IsWindow(hwnd) || !Native.BelongsTo(child, root)) throw new BookmarkException(ResultCode.ContextChanged);
            var application = scope.Get(window, "Application");
            var candidate = new ConnectedWindow(window, application, hwnd, pid, stamp);
            VerifyWindow(candidate, scope);
            if (connected.TryGetValue(hwnd, out var previous) && !SameWindow(previous.Window, window, scope))
                throw new BookmarkException(ResultCode.AmbiguousTarget);
            connected[hwnd] = candidate;
        }
        if (connected.Count != 1) throw new BookmarkException(connected.Count == 0 ? ResultCode.EnumerationIncomplete : ResultCode.AmbiguousTarget);
        context.Check(); return connected.Values.Single();
    }

    private static Position ReadPosition(ConnectedWindow connection, ComScope scope, RequestContext context, bool requireActive)
    {
        context.Check(); VerifyWindow(connection, scope);
        RequireSinglePane(connection.Window, scope);
        if (requireActive)
        {
            var activeWindow = scope.Get(connection.Application, "ActiveWindow");
            if (activeWindow is null || !SameWindow(activeWindow, connection.Window, scope)) throw new BookmarkException(ResultCode.ContextChanged);
        }
        var document = scope.Get(connection.Window, "Document");
        if (!ComScope.Same(scope.Get(document, "Application"), connection.Application) ||
            (requireActive && !ComScope.Same(scope.Get(connection.Application, "ActiveDocument"), document)))
            throw new BookmarkException(ResultCode.ContextChanged);
        VerifyDocumentWindow(document, connection, scope, context);
        if (string.IsNullOrWhiteSpace(scope.Text(document, "Path"))) throw new BookmarkException(ResultCode.UnsavedDocument);
        ValidateDocumentProtection(scope.Number(document, "ProtectionType"));
        var selection = scope.Get(connection.Window, "Selection");
        var range = scope.Get(selection, "Range");
        if (!ComScope.Same(scope.Get(range, "Document"), document)) throw new BookmarkException(ResultCode.ContextChanged);
        var story = scope.Number(selection, "StoryType");
        var type = scope.Number(selection, "Type");
        var start = scope.Number(selection, "Start");
        var end = scope.Number(selection, "End");
        var body = scope.Get(document, "Content");
        var documentEnd = scope.Number(body, "End");
        ValidateCaptureCoordinates(story, type, start, end, documentEnd);
        if (scope.Number(range, "StoryType") != story || scope.Number(range, "Start") != start || scope.Number(range, "End") != end)
            throw new BookmarkException(ResultCode.ContextChanged);
        var target = new CapturedTarget(TargetKind.WordPosition, scope.Text(document, "FullName"),
            HadUnsavedChanges: !scope.Flag(document, "Saved"), WordStart: start);
        PathPolicy.Validate(target);
        RequireLocalPath(target.Path);
        context.Check(); VerifyWindow(connection, scope);
        return new(target, document, end, type, documentEnd);
    }

    // Restrict Editing / No changes (read only) still permits reading and selecting body ranges.
    // Do not confuse it with Protected View, and never remove protection or enable editing.
    internal static void ValidateDocumentProtection(int protectionType)
    {
        if (protectionType is not (-1 or 3)) throw new BookmarkException(ResultCode.UnsupportedTarget); // wdNoProtection / wdAllowOnlyReading
    }

    // Word offsets are story-relative character coordinates, not displayed pages or semantic paragraph anchors.
    internal static void ValidateCaptureCoordinates(int story, int selectionType, int start, int end, int documentEnd)
    {
        if (story != 1 || selectionType is not (1 or 2)) throw new BookmarkException(ResultCode.UnsupportedTarget);
        if (start < 0 || end < start || documentEnd < 1 || end > documentEnd)
            throw new BookmarkException(ResultCode.ContextChanged);
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
                // Observe the single Shell request while Word creates its native document pane.
                System.Windows.Forms.Application.DoEvents(); Thread.Sleep(125); continue;
            }
            if (enumeration.Matches.Count == 1)
            {
                var match = enumeration.Matches[0];
                foreach (var candidate in match.Windows) guard.Permit(candidate.Hwnd);
                var selected = ChooseWindow(match, scope);
                context.TargetHwnd = selected.Hwnd.ToInt64();
                var nativeWindow = Connect(selected.Hwnd, scope, context, visibleOnly: false);
                if (!SameWindow(nativeWindow.Window, selected.Window, scope) || !ComScope.Same(nativeWindow.Application, match.Application) ||
                    !ComScope.Same(scope.Get(nativeWindow.Window, "Document"), match.Document))
                    throw new BookmarkException(ResultCode.ContextChanged);
                try
                {
                    RequireSinglePane(selected.Window, scope);
                    ValidatePosition(target, match.Document, scope, context);
                    if (validateOnly) return context.Response(ResultCode.Validated, target);
                    VerifyWindow(selected, scope); guard.Check(); context.Check();
                    context.ExternalActionStarted = true;
                    scope.Call(selected.Window, "Activate");
                    guard.Check(); context.Check(); VerifyWindow(selected, scope);
                    // Recreate the collapsed range after activation; never reuse an offset silently adjusted by Word.
                    RequireSinglePane(selected.Window, scope);
                    var range = ValidatePosition(target, match.Document, scope, context);
                    guard.Check(); context.Check();
                    scope.Call(range, "Select");
                    context.Check();
                    var actual = ReadPosition(selected, scope, context, requireActive: true);
                    if (!string.Equals(PathPolicy.Normalize(actual.Target.Path), normalized, StringComparison.Ordinal) ||
                        actual.Target.WordStart != target.WordStart || actual.SelectionEnd != target.WordStart)
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
                if (Type.GetTypeFromProgID("Word.Application") is null) throw new BookmarkException(ResultCode.UnsupportedTarget);
                guard.Check(); context.Check();
                WindowsAdapter.ShellOpen(target.Path, context);
                opened = true; // Never repeat Shell open after an uncertain outcome.
            }
            System.Windows.Forms.Application.DoEvents(); Thread.Sleep(125);
        }
    }

    private static object ValidatePosition(CapturedTarget target, object document, ComScope scope, RequestContext context)
    {
        context.Check();
        if (!string.Equals(PathPolicy.Normalize(scope.Text(document, "FullName")), PathPolicy.Normalize(target.Path), StringComparison.Ordinal))
            throw new BookmarkException(ResultCode.ContextChanged);
        ValidateDocumentProtection(scope.Number(document, "ProtectionType"));
        var body = scope.Get(document, "Content");
        var end = scope.Number(body, "End");
        if (target.WordStart is not int start || !CanRestoreOffset(start, end)) throw new BookmarkException(ResultCode.TargetUnavailable);
        var range = scope.Call(document, "Range", start, start);
        if (!ComScope.Same(scope.Get(range, "Document"), document) || scope.Number(range, "StoryType") != 1 ||
            scope.Number(range, "Start") != start || scope.Number(range, "End") != start)
            throw new BookmarkException(ResultCode.TargetUnavailable);
        context.Check(); return range;
    }

    internal static bool CanRestoreOffset(int start, int documentEnd) => start >= 0 && documentEnd >= 1 && start <= documentEnd;

    // Word collection Item uses DISPATCH_METHOD; PROPERTYGET raises 0x800A16E6 on installed Word.
    private static void VerifyDocumentWindow(object document, ConnectedWindow connection, ComScope scope, RequestContext context)
    {
        var windows = scope.Get(document, "Windows");
        var count = scope.Number(windows, "Count");
        var member = false;
        for (var i = 1; i <= count; i++)
        {
            context.Check();
            if (SameWindow(scope.Call(windows, "Item", i), connection.Window, scope)) member = true;
        }
        if (!member || scope.Number(windows, "Count") != count) throw new BookmarkException(ResultCode.ContextChanged);
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
        var processesBefore = WordProcesses();
        var roots = WordRoots();
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
            if (!Native.IsWindowVisible(root) && Native.Children(root, "_WwG", false).Count == 0)
            {
                hiddenControlPids.Add(pid); continue;
            }
            try
            {
                var connected = Connect(root, scope, context, visibleOnly: false);
                connections[ComScope.Identity(connected.Application)] = connected.Application;
                connectedPids.Add(pid);
            }
            catch (Exception ex)
            {
                context.Check();
                if (!Native.IsWindowVisible(root) && ex is BookmarkException { Code: ResultCode.EnumerationIncomplete })
                {
                    // A hidden startup/control root may retain only inaccessible implementation panes.
                    // Its process must still have another native connection and a complete COM inventory.
                    hiddenControlPids.Add(pid);
                    continue;
                }
                Incomplete("connection:" + ex.GetType().Name + ":" + ex.HResult);
            }
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
                var documents = scope.Get(application, "Documents");
                var count = scope.Number(documents, "Count");
                var identities = new List<nint>();
                for (var index = 1; index <= count; index++)
                {
                    context.Check();
                    var document = scope.Call(documents, "Item", index);
                    identities.Add(ComScope.Identity(document));
                    if (string.IsNullOrEmpty(scope.Text(document, "Path"))) continue;
                    var fullName = scope.Text(document, "FullName");
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
                    var windows = scope.Get(document, "Windows");
                    var windowCount = scope.Number(windows, "Count");
                    var verified = new List<ConnectedWindow>();
                    for (var windowIndex = 1; windowIndex <= windowCount; windowIndex++)
                    {
                        context.Check();
                        var window = scope.Call(windows, "Item", windowIndex);
                        var hwnd = WindowHandle(scope.Get(window, "Hwnd"));
                        Native.GetWindowThreadProcessId(hwnd, out var owner);
                        var stamp = Native.ProcessStamp(owner);
                        if (!Native.IsWindow(hwnd) || Native.Class(hwnd) != "OpusApp" || stamp == 0 || !connectedPids.Contains(owner) ||
                            !ComScope.Same(scope.Get(window, "Application"), application) || !ComScope.Same(scope.Get(window, "Document"), document))
                        { Incomplete("window-owner"); continue; }
                        verified.Add(new(window, application, hwnd, owner, stamp));
                    }
                    if (scope.Number(windows, "Count") != windowCount || verified.Count == 0) Incomplete("document-window-inventory");
                    matches[ComScope.Identity(document)] = new(document, application, verified);
                }
                if (scope.Number(documents, "Count") != count) Incomplete("document-count-changed");
                else for (var index = 1; index <= count; index++)
                {
                    context.Check();
                    if (ComScope.Identity(scope.Call(documents, "Item", index)) != identities[index - 1]) Incomplete("document-identity-changed");
                }
            }
            catch (BookmarkException) { throw; }
            catch (Exception ex) { context.Check(); Incomplete("document-enumeration:" + ex.GetType().Name + ":" + ex.HResult); }
        }
        if (!processesBefore.SetEquals(WordProcesses()) || !roots.ToHashSet().SetEquals(WordRoots())) Incomplete("process-root-inventory-changed");
        context.Check(); return new(matches.Values.ToList(), complete);
    }

    private static void RequireSinglePane(object window, ComScope scope)
    {
        // A split window has independent selections; the contract does not guess which pane to resume.
        if (scope.Number(scope.Get(window, "Panes"), "Count") != 1) throw new BookmarkException(ResultCode.UnsupportedTarget);
    }

    private static string RequireLocalPath(string path)
    {
        var normalized = PathPolicy.Normalize(path);
        if (normalized.Length < 3 || normalized[1] != ':') throw new BookmarkException(ResultCode.UnsupportedTarget);
        return normalized;
    }

    private static HashSet<uint> WordProcesses()
    {
        var found = new HashSet<uint>();
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName("WINWORD"))
        {
            using (process)
            {
                try { if (process.SessionId == current.SessionId) found.Add((uint)process.Id); }
                catch { throw new BookmarkException(ResultCode.EnumerationIncomplete); }
            }
        }
        return found;
    }

    private static List<nint> WordRoots()
    {
        var found = new List<nint>();
        Native.EnumWindows((hwnd, _) => { if (Native.Class(hwnd) == "OpusApp") found.Add(hwnd); return true; }, 0);
        return found;
    }

    private static void VerifyWindow(ConnectedWindow window, ComScope scope)
    {
        Native.GetWindowThreadProcessId(window.Hwnd, out var owner);
        if (!Native.IsWindow(window.Hwnd) || owner != window.ProcessId || Native.ProcessStamp(owner) != window.ProcessStamp ||
            WindowHandle(scope.Get(window.Window, "Hwnd")) != window.Hwnd || !ComScope.Same(scope.Get(window.Window, "Application"), window.Application))
            throw new BookmarkException(ResultCode.ContextChanged);
    }

    private static bool SameWindow(object first, object second, ComScope scope)
    {
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
