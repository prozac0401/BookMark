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
                var target = cls switch
                {
                    "CabinetWClass" or "ExploreWClass" => ExplorerAdapter.Capture(snapshot, context),
                    "XLMAIN" => ExcelAdapter.Capture(snapshot, context),
                    _ => throw new BookmarkException(ResultCode.UnsupportedTarget)
                };
                PathPolicy.Validate(target);
                context.Check();
                return context.Response(ResultCode.Captured, target);
            }
            if (request.Operation is not (Operation.Resume or Operation.ValidateRelink)) throw new BookmarkException(ResultCode.InvalidRequest);
            var saved = PathPolicy.Validate(request.Target ?? throw new BookmarkException(ResultCode.InvalidRequest));
            if (saved.Kind == TargetKind.ExcelCell) return ExcelAdapter.Resume(saved, context, request.Operation == Operation.ValidateRelink);
            using var guard = request.Operation == Operation.Resume ? new ResumeGuard(request.Snapshot) : null;
            CheckExists(saved);
            if (request.Operation == Operation.ValidateRelink) return context.Response(ResultCode.Validated, saved);
            guard!.Check(); context.Check();
            if (saved.Kind == TargetKind.Folder || PathPolicy.ShouldOpenDocument(saved.Path))
            {
                ShellOpen(saved.Path, context);
                return context.Response(ResultCode.OpenRequested);
            }
            Reveal(saved.Path, context);
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
            if (context.ExternalActionStarted && code is not (ResultCode.OpenedPositionFailed or ResultCode.PositionRestoredFocusPending or ResultCode.PositionRestored))
                code = ResultCode.ResumeOutcomeUnknown;
            return context.Response(code);
        }
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
    private static void Reveal(string path, RequestContext context)
    {
        var parent = Path.GetDirectoryName(path) ?? throw new BookmarkException(ResultCode.TargetUnavailable);
        nint folder = 0, item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(Native.SHParseDisplayName(parent, 0, out folder, 0, out _));
            Marshal.ThrowExceptionForHR(Native.SHParseDisplayName(path, 0, out item, 0, out _));
            context.Check(); context.ExternalActionStarted = true;
            var result = Native.SHOpenFolderAndSelectItems(folder, 1, [Native.ILFindLastID(item)], 0);
            if (result < 0) { context.ExternalActionStarted = false; Marshal.ThrowExceptionForHR(result); }
        }
        finally { if (folder != 0) Marshal.FreeCoTaskMem(folder); if (item != 0) Marshal.FreeCoTaskMem(item); }
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
