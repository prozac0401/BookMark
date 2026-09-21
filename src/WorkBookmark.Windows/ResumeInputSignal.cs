using WorkBookmark.Core;

namespace WorkBookmark.Windows;

/// <summary>Ephemeral, user/session-scoped cancellation latch that bridges worker startup.</summary>
public sealed class ResumeInputSignal : IDisposable
{
    private readonly EventWaitHandle handle;
    private static NamedWaitHandleOptions Options => new() { CurrentUserOnly = true, CurrentSessionOnly = true };
    private ResumeInputSignal(EventWaitHandle handle) => this.handle = handle;
    private static string Name(Guid requestId)
    {
        if (requestId == Guid.Empty) throw new BookmarkException(ResultCode.InvalidRequest);
        return @"Local\WorkBookmark.ResumeInput." + requestId.ToString("N");
    }

    public static ResumeInputSignal Create(Guid requestId)
    {
        var handle = new EventWaitHandle(false, EventResetMode.ManualReset, Name(requestId), Options, out bool created);
        if (!created)
        {
            handle.Dispose();
            throw new BookmarkException(ResultCode.Cancelled);
        }
        return new(handle);
    }

    internal static ResumeInputSignal OpenExisting(Guid requestId)
    {
        try { return new(EventWaitHandle.OpenExisting(Name(requestId), Options)); }
        catch (Exception error) when (error is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            // A requested input guard must never degrade to an unguarded operation.
            throw new BookmarkException(ResultCode.Cancelled);
        }
    }

    public void MarkInput() => handle.Set();
    internal bool HasInput => handle.WaitOne(0);
    public void Dispose() => handle.Dispose();
}
