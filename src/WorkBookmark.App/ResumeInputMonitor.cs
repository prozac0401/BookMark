using WorkBookmark.Windows;

namespace WorkBookmark.App;

/// <summary>Dispatches input cancellation outside the low-level hook and onto the owning UI.</summary>
internal sealed class ResumeInputMonitor : IDisposable
{
    private readonly InputPressObserver _observer;
    private readonly SynchronizationContext _owner;
    private readonly Action _cancel;
    private bool _notified;
    private volatile bool _disposed;

    public bool HasNewInput => _observer.HasNewInput;

    public ResumeInputMonitor(Action cancel, ResumeInputSignal? signal = null)
    {
        _cancel = cancel;
        _owner = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _observer = new InputPressObserver(QueueNotification, signal);
    }

    private void QueueNotification()
    {
        if (_disposed) return;
        try { _owner.Post(_ => Notify(), null); }
        // The owner may be closing before the queued observer notification arrives.
        // The input latch remains set; never fail the process on this ThreadPool thread.
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.InvalidAsynchronousStateException) { }
    }

    private void Notify()
    {
        if (_disposed || _notified) return;
        _notified = true;
        _cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _observer.Dispose();
        // Close the acceptance race before capture commits its immutable observation.
        // This executes on the owner, never inside a low-level hook callback.
        if (HasNewInput) Notify();
        _disposed = true;
    }
}
