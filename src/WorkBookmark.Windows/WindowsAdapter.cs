using System.Reflection;
using System.Runtime.InteropServices;
using WorkBookmark.Core;

namespace WorkBookmark.Windows;

public static class WindowsAdapter
{
    /// <summary>Call only in the isolated request worker's STA. All COM and filesystem access stays there.</summary>
    public static WorkerResponse Execute(WorkerRequest request)
    {
        var context = new RequestContext(request);
        try
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA || request.ProtocolVersion != 1 || request.RequestId == Guid.Empty)
                return context.Response(ResultCode.InvalidRequest);
            context.Check();
            if (request.Operation == Operation.Capture)
            {
                var snapshot = request.Snapshot ?? throw new BookmarkException(ResultCode.InvalidRequest);
                if (!Native.IsUnelevatedProcess(snapshot.ProcessId)) throw new BookmarkException(ResultCode.UnsupportedTarget);
                var cls = Native.Class((nint)snapshot.Hwnd);
                if (cls == "Notepad") return NotepadAdapter.Capture(snapshot, context);
                var target = cls switch
                {
                    "CabinetWClass" or "ExploreWClass" => ExplorerAdapter.Capture(snapshot, context),
                    "XLMAIN" => ExcelAdapter.Capture(snapshot, context),
                    "OpusApp" => WordAdapter.Capture(snapshot, context),
                    "PPTFrameClass" => PowerPointAdapter.Capture(snapshot, context),
                    "Chrome_WidgetWin_1" when IsSupportedBrowser(snapshot.ProcessId) => BrowserAdapter.Capture(snapshot, context),
                    PdfAdapter.FrameClass => PdfAdapter.Capture(snapshot, context),
                    _ => throw new BookmarkException(ResultCode.UnsupportedTarget)
                };
                PathPolicy.Validate(target);
                context.Check();
                return context.Response(ResultCode.Captured, target);
            }
            if (request.Operation is not (Operation.Resume or Operation.ValidateRelink)) throw new BookmarkException(ResultCode.InvalidRequest);
            var saved = PathPolicy.Validate(request.Target ?? throw new BookmarkException(ResultCode.InvalidRequest));
            if (saved.Kind == TargetKind.ExcelCell) return ExcelAdapter.Resume(saved, context, request.Operation == Operation.ValidateRelink);
            if (saved.Kind == TargetKind.WordPosition) return WordAdapter.Resume(saved, context, request.Operation == Operation.ValidateRelink);
            if (saved.Kind == TargetKind.PowerPointSlide) return PowerPointAdapter.Resume(saved, context, request.Operation == Operation.ValidateRelink);
            if (saved.Kind == TargetKind.PdfPage) return PdfAdapter.Resume(saved, context, request.Operation == Operation.ValidateRelink);
            if (saved.Kind is TargetKind.NotepadPosition or TargetKind.NotepadSnapshot) return NotepadAdapter.Resume(saved, context, request.Operation == Operation.ValidateRelink);
            if (saved.Kind == TargetKind.WebPage)
            {
                if (request.Operation != Operation.Resume) throw new BookmarkException(ResultCode.UnsupportedTarget);
                using var browserGuard = new ResumeGuard(request.Snapshot);
                browserGuard.Check(); context.Check();
                context.ExternalActionStarted = true;
                var opened = Native.ShellExecute(0, "open", saved.Path, null, null, 1).ToInt64();
                if (opened <= 32) { context.ExternalActionStarted = false; throw new BookmarkException(ResultCode.TargetUnavailable); }
                return context.Response(ResultCode.OpenRequested);
            }
            using var guard = request.Operation == Operation.Resume ? new ResumeGuard(request.Snapshot) : null;
            CheckExists(saved);
            if (request.Operation == Operation.ValidateRelink) return context.Response(ResultCode.Validated, saved);
            guard!.Check(); context.Check();
            if (saved.Kind == TargetKind.Folder || PathPolicy.ShouldOpenDocument(saved.Path))
            {
                ShellOpen(saved.Path, context);
                return context.Response(ResultCode.OpenRequested);
            }
            Reveal(saved.Path, context, guard.Check);
            return context.Response(ResultCode.RevealRequested);
        }
        catch (Exception exception)
        {
            while (exception is TargetInvocationException { InnerException: not null } invocation) exception = invocation.InnerException!;
            var code = exception switch
            {
                BookmarkException error => error.Code,
                COMException com when com.HResult is unchecked((int)0x80010001) or unchecked((int)0x8001010A) => ResultCode.AppBusy,
                UnauthorizedAccessException or IOException => ResultCode.TargetUnavailable,
                _ => ResultCode.UnsupportedTarget
            };
            if (request.Target?.Kind == TargetKind.NotepadSnapshot)
                NotepadAdapter.SnapshotTrace?.Invoke("snapshot worker failure=" + code + " exception=" + exception.GetType().Name + " HRESULT=" + exception.HResult.ToString("X8"));
            bool webOfficeResume = request.Operation == Operation.Resume && request.Target is { } target && OfficeLocation.IsWebTarget(target);
            if (webOfficeResume && context.TargetHwnd != 0 &&
                code is not (ResultCode.OpenedPositionFailed or ResultCode.PositionRestoredFocusPending or ResultCode.PositionRestored))
                code = ResultCode.OfficeDocumentOpened;
            else if (context.ExternalActionStarted && code is not (ResultCode.OpenedPositionFailed or ResultCode.PositionRestoredFocusPending or ResultCode.PositionRestored))
                code = webOfficeResume ? ResultCode.OfficeResumePending : ResultCode.ResumeOutcomeUnknown;
            return context.Response(code);
        }
    }
    private static bool IsSupportedBrowser(uint processId)
    {
        using var process = System.Diagnostics.Process.GetProcessById(checked((int)processId));
        return process.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
            process.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase);
    }
    internal static void CheckExists(CapturedTarget target)
    {
        var exists = target.Kind == TargetKind.Folder ? Directory.Exists(target.Path) : File.Exists(target.Path);
        if (!exists) throw new BookmarkException(ResultCode.TargetUnavailable);
    }
    internal static void ShellOpen(string path, RequestContext context)
    {
        PathPolicy.Normalize(path); context.Check();
        context.ExternalActionStarted = true;
        // Separate verb/path arguments; no command interpreter or command string.
        var result = Native.ShellExecute(0, "open", path, null, null, 1).ToInt64();
        if (result <= 32) { context.ExternalActionStarted = false; throw new BookmarkException(ResultCode.TargetUnavailable); }
    }
    internal static void Reveal(string path, RequestContext context, Action checkInteraction, IShellSelectionApi? api = null)
    {
        api ??= NativeShellSelectionApi.Instance;
        var parent = Path.GetDirectoryName(path) ?? throw new BookmarkException(ResultCode.TargetUnavailable);
        nint folder = 0, item = 0;
        try
        {
            folder = api.Parse(parent);
            item = api.Parse(path);
            // Parsing may wait on Shell extensions or a network location. Recheck after that wait.
            checkInteraction();
            context.Check(); context.ExternalActionStarted = true;
            var result = api.Open(folder, item);
            if (result < 0) { context.ExternalActionStarted = false; Marshal.ThrowExceptionForHR(result); }
        }
        finally { if (folder != 0) api.Free(folder); if (item != 0) api.Free(item); }
    }
}

internal sealed class RequestContext(WorkerRequest request)
{
    internal WorkerRequest Request { get; } = request;
    internal bool ExternalActionStarted { get; set; }
    internal long TargetHwnd { get; set; }
    internal void Check()
    {
        if (DateTimeOffset.UtcNow >= Request.DeadlineUtc)
            throw new BookmarkException(Request.Operation == Operation.Capture ? ResultCode.CaptureTimedOut : ExternalActionStarted ? ResultCode.ResumeOutcomeUnknown : ResultCode.Cancelled);
    }
    internal WorkerResponse Response(ResultCode code, CapturedTarget? target = null) => new(1, Request.RequestId, code, target, ExternalActionStarted, TargetHwnd);
}

/// <summary>The narrow Shell boundary lets regression tests delay parsing without opening user windows.</summary>
internal interface IShellSelectionApi
{
    nint Parse(string path);
    int Open(nint folder, nint item);
    void Free(nint item);
}

internal sealed class NativeShellSelectionApi : IShellSelectionApi
{
    internal static readonly NativeShellSelectionApi Instance = new();
    public nint Parse(string path)
    {
        var result = Native.SHParseDisplayName(path, 0, out var item, 0, out _);
        if (result < 0)
        {
            if (item != 0) Marshal.FreeCoTaskMem(item);
            Marshal.ThrowExceptionForHR(result);
        }
        return item;
    }
    public int Open(nint folder, nint item) => Native.SHOpenFolderAndSelectItems(folder, 1, [Native.ILFindLastID(item)], 0);
    public void Free(nint item) => Marshal.FreeCoTaskMem(item);
}
