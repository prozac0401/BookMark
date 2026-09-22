using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using WorkBookmark.App;
using WorkBookmark.Windows;

namespace WorkBookmark.Desktop.Tests;

internal static class InputMonitorChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<bool, string> assert)
    {
        int ownerThread = Environment.CurrentManagedThreadId, calls = 0, callbackThread = 0;
        using var callbackStarted = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        using var callbackFinished = new ManualResetEventSlim();
        using (var observer = new InputPressObserver(() =>
        {
            callbackThread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref calls);
            callbackStarted.Set();
            releaseCallback.Wait(TimeSpan.FromSeconds(5));
            callbackFinished.Set();
        }))
        {
            try
            {
                Thread hookThread = Get<Thread>(observer, "thread");
                assert(hookThread.IsBackground && hookThread.IsAlive && hookThread.ManagedThreadId != ownerThread,
                    "input hooks run on a dedicated background message loop");
                Inject(observer, "Keyboard", 0x0101); Inject(observer, "Keyboard", 0x0105);
                Inject(observer, "Mouse", 0x0200); Inject(observer, "Mouse", 0x0202);
                assert(!observer.HasNewInput, "releasing the initiating key and moving the mouse do not interrupt a request");

                var timer = Stopwatch.StartNew();
                Inject(observer, "Keyboard", 0x0100);
                assert(timer.ElapsedMilliseconds < 500 && observer.HasNewInput,
                    "input latches immediately and a blocking cancellation action cannot hold the hook callback");
                assert(callbackStarted.Wait(TimeSpan.FromSeconds(2)) && callbackThread != ownerThread && callbackThread != hookThread.ManagedThreadId,
                    "cancellation work runs outside both the hook callback and its message loop");
                timer.Restart();
                for (int i = 0; i < 100; i++) Inject(observer, "Mouse", 0x020A);
                assert(timer.ElapsedMilliseconds < 500 && calls == 1,
                    "additional input remains responsive while cancellation is blocked and notification is one-shot");
                observer.Dispose();
                assert(!hookThread.IsAlive, "disposing the observer stops its hook message loop even with a callback blocked");
            }
            finally { releaseCallback.Set(); callbackFinished.Wait(TimeSpan.FromSeconds(2)); }
        }

        var originalContext = SynchronizationContext.Current;
        var context = new QueuedContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            using var signal = ResumeInputSignal.Create(Guid.NewGuid());
            int notifications = 0, notificationThread = 0;
            Type monitorType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.ResumeInputMonitor")!;
            using var monitor = (IDisposable)Activator.CreateInstance(monitorType,
                new Action(() => { notifications++; notificationThread = Environment.CurrentManagedThreadId; }), signal)!;
            var observer = Get<InputPressObserver>(monitor, "_observer");
            Inject(observer, "Mouse", 0x0201);
            assert((bool)typeof(ResumeInputSignal).GetProperty("HasInput", PrivateInstance)!.GetValue(signal)! && notifications == 0,
                "worker input signal is set before the UI can process queued cancellation");
            assert(SpinWait.SpinUntil(() => !context.Pending.IsEmpty, TimeSpan.FromSeconds(2)),
                "input cancellation is posted to the original UI context");
            context.Drain();
            assert(notifications == 1 && notificationThread == ownerThread,
                "the original UI thread performs cancellation exactly once");
            monitor.Dispose();
            Inject(observer, "Keyboard", 0x0104); context.Drain();
            assert(notifications == 1, "late callbacks cannot cancel a completed request");

            // Input may beat the worker result while the owner has not dispatched its
            // queued cancellation. Disposal must close that capture acceptance race.
            using var lateMonitor = (IDisposable)Activator.CreateInstance(monitorType, new Action(() => notifications++), null)!;
            Inject(Get<InputPressObserver>(lateMonitor, "_observer"), "Mouse", 0x020B);
            lateMonitor.Dispose(); context.Drain();
            assert(notifications == 2, "capture disposal flushes already observed input on its owner exactly once");
        }
        finally { SynchronizationContext.SetSynchronizationContext(originalContext); }

        Type guardType = typeof(InputPressObserver).Assembly.GetType("WorkBookmark.Windows.ResumeGuard")!;
        Guid requestId = Guid.NewGuid();
        using var sharedSignal = ResumeInputSignal.Create(requestId);
        using (var guard = (IDisposable)Activator.CreateInstance(guardType, PrivateInstance, null, [null, requestId], null)!)
            assert(guardType.GetField("inputObserver", PrivateInstance)!.GetValue(guard) is null,
                "a monitored worker uses only its shared input signal and installs no input hooks on its COM thread");
        using (var guard = (IDisposable)Activator.CreateInstance(guardType, PrivateInstance, null, [null, null], null)!)
        {
            var observer = Get<InputPressObserver>(guard, "inputObserver");
            assert(Get<Thread>(observer, "thread").ManagedThreadId != ownerThread,
                "standalone workers also keep input hooks off their COM thread");
        }

        using var endedSignal = ResumeInputSignal.Create(Guid.NewGuid());
        using (var observer = new InputPressObserver(inputSignal: endedSignal))
        {
            assert(PostThreadMessage(Get<uint>(observer, "threadId"), 0x0012, 0, 0) &&
                Get<Thread>(observer, "thread").Join(TimeSpan.FromSeconds(2)) && observer.HasNewInput &&
                (bool)typeof(ResumeInputSignal).GetProperty("HasInput", PrivateInstance)!.GetValue(endedSignal)!,
                "an unexpected observer exit closes both the local guard and the worker's shared input signal");
        }
    }

    private static T Get<T>(object instance, string field) => (T)instance.GetType().GetField(field, PrivateInstance)!.GetValue(instance)!;
    private static void Inject(InputPressObserver observer, string method, int message) =>
        typeof(InputPressObserver).GetMethod(method, PrivateInstance)!.Invoke(observer, [0, (nint)message, (nint)0]);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    private sealed class QueuedContext : SynchronizationContext
    {
        internal readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> Pending = new();
        public override void Post(SendOrPostCallback callback, object? state) => Pending.Enqueue((callback, state));
        internal void Drain() { while (Pending.TryDequeue(out var item)) item.Callback(item.State); }
    }
}
