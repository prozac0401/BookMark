using System.Collections.Concurrent;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using WorkBookmark.App;
using WorkBookmark.Core;

namespace WorkBookmark.Desktop.Tests;

// Uses the real manager and placement capture. A gated storage callback and a
// queued UI context make the write/continuation race deterministic without
// sleeping through debounce timers or touching the user's application database.
internal static class StickerPersistenceChecks
{
    internal static void Run(Action<bool, string> assert)
    {
        ChangedDuringWrite(assert);
        FailedWriteThenNewPlacement(assert);
        ShutdownBeforeContinuation(assert, failFirst: false);
        ShutdownBeforeContinuation(assert, failFirst: true);
    }

    private static void ChangedDuringWrite(Action<bool, string> assert)
    {
        using var fixture = new Fixture();
        var original = fixture.Place(400);
        Task first = fixture.Save();
        fixture.WaitForFirstWrite();
        var latest = fixture.Place(480);
        Task busyAttempt = fixture.Save();
        assert(busyAttempt.IsCompletedSuccessfully && fixture.SaveAttempts == 1,
            "SP01 moving during a pending layout write does not start a competing storage write");
        fixture.ReleaseFirstWrite(first);
        fixture.Dispatch(first);
        fixture.Dispatch(fixture.Save());
        assert(fixture.FirstBatch.Single() == original && fixture.Saved == latest && fixture.SaveAttempts == 2,
            "SP02 a placement changed during the first batch is persisted by the next batch without reverting");
    }

    private static void FailedWriteThenNewPlacement(Action<bool, string> assert)
    {
        using var fixture = new Fixture(failFirst: true);
        fixture.Place(400);
        Task first = fixture.Save();
        fixture.WaitForFirstWrite();
        fixture.ReleaseFirstWrite(first);
        // Storage has failed, but its UI continuation has not yet requeued the
        // failed batch. The new placement must win against that stale batch.
        var latest = fixture.Place(500);
        fixture.Dispatch(first);
        assert(fixture.Notifications == 1 && fixture.Saved is null,
            "SP03 failed layout persistence reports once and does not claim a successful save");
        fixture.Dispatch(fixture.Save());
        assert(fixture.Saved == latest && fixture.SaveAttempts == 2 && fixture.Notifications == 1,
            "SP04 movement after a failed write retries the newest placement rather than the failed snapshot");
    }

    private static void ShutdownBeforeContinuation(Action<bool, string> assert, bool failFirst)
    {
        using var fixture = new Fixture(failFirst);
        fixture.Place(400);
        Task first = fixture.Save();
        fixture.WaitForFirstWrite();
        fixture.ReleaseFirstWrite(first);
        var latest = fixture.Place(520);
        fixture.DisposeManager();
        assert(fixture.Saved == latest && fixture.SaveAttempts == 2,
            failFirst
                ? "SP05 shutdown flushes the latest layout when a failed write has not yet reached its UI continuation"
                : "SP06 shutdown flushes newer movement after storage completes but before its UI continuation runs");
        fixture.Dispatch(first);
        assert(fixture.Saved == latest && fixture.SaveAttempts == 2 && fixture.Notifications == 0,
            failFirst
                ? "SP07 a delayed failed-write continuation cannot notify or rewrite after manager disposal"
                : "SP08 a delayed successful-write continuation cannot overwrite the final shutdown placement");
    }

    private sealed class Fixture : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Assembly AppAssembly = typeof(BookmarkApplicationContext).Assembly;
        private readonly object _manager;
        private readonly Form _form;
        private readonly Guid _id = Guid.NewGuid();
        private readonly bool _failFirst;
        private readonly ManualResetEventSlim _firstStarted = new();
        private readonly ManualResetEventSlim _releaseFirst = new();
        private readonly object _savedLock = new();
        private readonly QueuedContext _context = new();
        private readonly SynchronizationContext? _previousContext;
        private StickerLayout? _saved;
        private int _saveAttempts;
        internal int SaveAttempts => Volatile.Read(ref _saveAttempts);
        internal int Notifications { get; private set; }
        internal StickerLayout[] FirstBatch { get; private set; } = [];
        internal StickerLayout? Saved { get { lock (_savedLock) return _saved; } }

        internal Fixture(bool failFirst = false)
        {
            _failFirst = failFirst;
            var now = DateTimeOffset.UtcNow;
            var target = new CapturedTarget(TargetKind.File, @"C:\synthetic\sticker-save-race.txt");
            var bookmark = new Bookmark(_id, target, target.Path, "합성 배치 검사", "", now, now, 1, null, null, null, null);
            _form = (Form)Activator.CreateInstance(AppAssembly.GetType("WorkBookmark.App.UI.StickerForm")!, bookmark)!;
            _ = _form.Handle;
            _previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(_context);
            Func<Task<(IReadOnlyList<Bookmark> Items, IReadOnlyList<StickerLayout> Layouts)>> load =
                () => Task.FromResult<(IReadOnlyList<Bookmark>, IReadOnlyList<StickerLayout>)>(([], []));
            var constructor = AppAssembly.GetType("WorkBookmark.App.StickerManager")!.GetConstructors(Private).Single();
            _manager = constructor.Invoke([load, (Action<IReadOnlyList<StickerLayout>>)SaveBatch, (Action<string>)(_ => Notifications++)]);
        }

        internal StickerLayout Place(int width)
        {
            float scale = _form.DeviceDpi / 96F;
            _form.Bounds = new Rectangle(80, 80, (int)Math.Round(width * scale), (int)Math.Round(400 * scale));
            Invoke("Remember", _form);
            // This suite covers ordering and durability; coordinate conversion is
            // verified separately in StickerIntegrationChecks.
            return Field<Dictionary<Guid, StickerLayout>>("_layouts")[_id];
        }

        internal Task Save() => (Task)Invoke("SavePendingAsync")!;
        internal void WaitForFirstWrite()
        {
            if (!_firstStarted.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("The gated layout write did not start.");
        }

        internal void ReleaseFirstWrite(Task continuation)
        {
            Task write = Field<Task>("_writeTask");
            _releaseFirst.Set();
            if (Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(3))).GetAwaiter().GetResult() != write)
                throw new TimeoutException("The gated storage write did not finish.");
            // Do not run the captured continuation until the scenario explicitly
            // dispatches it; shutdown tests depend on this precise boundary.
            if (!SpinWait.SpinUntil(() => _context.HasWork || continuation.IsCompleted, TimeSpan.FromSeconds(3)))
                throw new TimeoutException("The write continuation was not queued.");
            if (continuation.IsCompleted) throw new InvalidOperationException("The write escaped the queued UI context.");
        }

        internal void Dispatch(Task operation)
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!operation.IsCompleted && DateTime.UtcNow < deadline)
            {
                _context.Drain();
                Thread.Sleep(1);
            }
            if (!operation.IsCompleted) throw new TimeoutException("The layout continuation did not finish.");
            operation.GetAwaiter().GetResult();
        }

        internal void DisposeManager() => ((IDisposable)_manager).Dispose();

        private void SaveBatch(IReadOnlyList<StickerLayout> layouts)
        {
            int attempt = Interlocked.Increment(ref _saveAttempts);
            if (attempt == 1)
            {
                FirstBatch = layouts.ToArray();
                _firstStarted.Set();
                if (!_releaseFirst.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The test did not release the first layout write.");
                if (_failFirst) throw new IOException("Injected sticker placement persistence failure.");
            }
            lock (_savedLock) _saved = layouts.Single(value => value.BookmarkId == _id);
        }

        private object? Invoke(string method, params object[] arguments) => _manager.GetType().GetMethod(method, Private)!.Invoke(_manager, arguments);
        private T Field<T>(string name) => (T)_manager.GetType().GetField(name, Private)!.GetValue(_manager)!;

        public void Dispose()
        {
            _releaseFirst.Set();
            try
            {
                DisposeManager();
                _context.Drain();
                _form.Dispose();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(_previousContext);
                _firstStarted.Dispose();
                _releaseFirst.Dispose();
            }
        }
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        internal bool HasWork => !_callbacks.IsEmpty;
        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue((callback, state));
        internal void Drain()
        {
            while (_callbacks.TryDequeue(out var work)) work.Callback(work.State);
        }
    }
}
