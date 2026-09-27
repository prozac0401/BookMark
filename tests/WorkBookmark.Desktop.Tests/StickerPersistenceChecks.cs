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
        PresentationUpgradeFailures(assert);
        foreach (int version in new[] { 1, 2 }) LegacyUpgradeRetries(version, assert);
        EditingWhilePlacementWriteCompletes(assert);
        ChangedDuringWrite(assert);
        FailedWriteThenNewPlacement(assert);
        ShutdownBeforeContinuation(assert, failFirst: false);
        ShutdownBeforeContinuation(assert, failFirst: true);
    }

    private static void PresentationUpgradeFailures(Action<bool, string> assert)
    {
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var assembly = typeof(UserSettings).Assembly;
        var managerType = assembly.GetType("WorkBookmark.App.StickerManager")!;
        var upgrade = assembly.GetType("WorkBookmark.App.StickerPresentationUpgrade")!
            .GetMethod("ApplyAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        string directory = Path.Combine(Path.GetTempPath(), "WorkBookmark-PresentationQa-" + Guid.NewGuid().ToString("N"));
        var legacy = UserSettings.Default with { DisplayMode = BookmarkDisplayMode.List, IntroShown = true };
        legacy.Save(directory);
        var size = (Size)managerType.GetProperty("DefaultExpandedSize96", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var stored = new[]
        {
            new StickerLayout(Guid.NewGuid(), "synthetic-screen", 20, 30, 440, 400),
            new StickerLayout(Guid.NewGuid(), "synthetic-screen", 40, 50, 480, 420, true)
        };
        int batches = 0, settingsSaves = 0;
        bool failLayouts = true, failSettings = true;
        Func<Task<(IReadOnlyList<Bookmark> Items, IReadOnlyList<StickerLayout> Layouts)>> load =
            () => Task.FromResult<(IReadOnlyList<Bookmark>, IReadOnlyList<StickerLayout>)>(([], stored.ToArray()));
        Action<IReadOnlyList<StickerLayout>> save = batch =>
        {
            Interlocked.Increment(ref batches);
            stored[0] = batch[0];
            if (failLayouts) throw new IOException("Injected partial layout save failure");
            stored[1] = batch[1];
        };
        using var manager = (IDisposable)managerType.GetConstructors(privateInstance).Single()
            .Invoke([load, save, (Action<string>)(_ => { })]);
        Func<Task> reset = () => (Task)managerType.GetMethod("ResetSavedSizesAsync", privateInstance)!.Invoke(manager, [0])!;
        Func<UserSettings, Task> persist = value =>
        {
            settingsSaves++;
            if (failSettings) return Task.FromException(new IOException("Injected settings save failure"));
            value.Save(directory);
            return Task.CompletedTask;
        };
        Task<UserSettings> Apply(UserSettings value) => (Task<UserSettings>)upgrade.Invoke(null, [value, reset, persist])!;
        bool Failed(Task task)
        {
            try { Complete(task); return false; }
            catch (IOException) { return true; }
        }
        assert(Failed(Apply(legacy)) && UserSettings.Load(directory) == legacy && settingsSaves == 0,
            "SP09 a partial layout-save failure cannot write the completion marker or switch the saved mode");
        failLayouts = false;
        assert(Failed(Apply(legacy)) && UserSettings.Load(directory) == legacy && settingsSaves == 1 &&
            stored.All(layout => layout.Width == size.Width && layout.Height == size.Height),
            "SP10 settings-save failure leaves the old completion marker after all layout saves succeed");
        failSettings = false;
        var retry = Apply(UserSettings.Load(directory));
        Complete(retry);
        assert(retry.Result.DisplayMode == BookmarkDisplayMode.Stickers &&
            retry.Result.StickerPresentationVersion == UserSettings.CurrentStickerPresentationVersion &&
            UserSettings.Load(directory) == retry.Result && batches == 3 && settingsSaves == 2 &&
            stored[0].Left == 20 && stored[1].Top == 50 && stored[1].IsCollapsed,
            "SP11 retry safely completes partial layout and settings failures while preserving placement and collapse");
        var selected = retry.Result with { DisplayMode = BookmarkDisplayMode.List };
        var again = Apply(selected);
        Complete(again);
        assert(again.Result == selected && batches == 3 && settingsSaves == 2,
            "SP12 completed presentation does not repeat layout writes or reset a later List choice");
    }

    private static void LegacyUpgradeRetries(int previousVersion, Action<bool, string> assert)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var assembly = typeof(UserSettings).Assembly;
        var managerType = assembly.GetType("WorkBookmark.App.StickerManager")!;
        var upgrade = assembly.GetType("WorkBookmark.App.StickerPresentationUpgrade")!.GetMethod("ApplyAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldDefault = (Size)managerType.GetProperty(previousVersion == 1 ? "LegacyDefaultExpandedSize96" : "CompactDefaultExpandedSize96",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var newDefault = (Size)managerType.GetProperty("DefaultExpandedSize96", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        string directory = Path.Combine(Path.GetTempPath(), "WorkBookmark-CompactRetry-" + Guid.NewGuid().ToString("N"));
        var settings = UserSettings.Default with { DisplayMode = BookmarkDisplayMode.List, IntroShown = true, StickerPresentationVersion = previousVersion };
        settings.Save(directory);
        // A version-one custom window can happen to match the later version-two default.
        // It must remain custom when that older installation upgrades directly.
        var customSize = previousVersion == 1
            ? (Size)managerType.GetProperty("CompactDefaultExpandedSize96", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!
            : new Size(oldDefault.Width + 110, oldDefault.Height + 70);
        var custom = new StickerLayout(Guid.NewGuid(), "synthetic-screen", 40, 50, customSize.Width, customSize.Height, true);
        // Native 144 v2 outer 410x152 was recorded as 273x101. Verify migration of
        // that saved data independently of the monitor running this test.
        var recordedDpi = new StickerLayout(Guid.NewGuid(), "synthetic-screen", 60, 70, 273, 101);
        var expectedRecordedDpi = previousVersion == 2
            ? recordedDpi with { Width = newDefault.Width, Height = newDefault.Height } : recordedDpi;
        var stored = new[] { new StickerLayout(Guid.NewGuid(), "synthetic-screen", 20, 30, oldDefault.Width, oldDefault.Height), custom, recordedDpi };
        bool failLayouts = true, failSettings = true;
        int settingsSaves = 0;
        Func<Task<(IReadOnlyList<Bookmark> Items, IReadOnlyList<StickerLayout> Layouts)>> load =
            () => Task.FromResult<(IReadOnlyList<Bookmark>, IReadOnlyList<StickerLayout>)>(([], stored.ToArray()));
        Action<IReadOnlyList<StickerLayout>> save = batch =>
        {
            stored[0] = batch[0];
            if (failLayouts) throw new IOException($"Injected version-{previousVersion} partial layout failure");
            stored[1] = batch[1];
            stored[2] = batch[2];
        };
        using var manager = (IDisposable)managerType.GetConstructors(instance).Single().Invoke([load, save, (Action<string>)(_ => { })]);
        Func<Task> reset = () => (Task)managerType.GetMethod("ResetSavedSizesAsync", instance)!.Invoke(manager, [previousVersion])!;
        Func<UserSettings, Task> persist = value =>
        {
            settingsSaves++;
            if (failSettings) return Task.FromException(new IOException($"Injected version-{previousVersion} settings failure"));
            value.Save(directory);
            return Task.CompletedTask;
        };
        Task<UserSettings> Apply() => (Task<UserSettings>)upgrade.Invoke(null, [UserSettings.Load(directory), reset, persist])!;
        bool Failed(Task task) { try { Complete(task); return false; } catch (IOException) { return true; } }
        assert(Failed(Apply()) && UserSettings.Load(directory) == settings && settingsSaves == 0 && stored[1] == custom && stored[2] == recordedDpi,
            $"SP13 a partial version-{previousVersion} upgrade leaves the marker and custom outer bounds unchanged");
        failLayouts = false;
        assert(Failed(Apply()) && UserSettings.Load(directory) == settings && stored[1] == custom &&
            stored[0].Width == newDefault.Width && stored[0].Height == newDefault.Height && stored[2] == expectedRecordedDpi,
            $"SP14 version-{previousVersion} settings failure after resizing recorded defaults preserves custom bounds and remains retryable");
        failSettings = false;
        Task<UserSettings> retry = Apply();
        Complete(retry);
        assert(retry.Result.DisplayMode == BookmarkDisplayMode.List &&
            retry.Result.StickerPresentationVersion == UserSettings.CurrentStickerPresentationVersion && settingsSaves == 2 &&
            stored[0].Width == newDefault.Width && stored[0].Height == newDefault.Height && stored[0].Left == 20 && stored[0].Top == 30 && stored[1] == custom && stored[2] == expectedRecordedDpi,
            $"SP15 retrying version {previousVersion} preserves migrated stored 150% defaults and custom bounds without resetting the List choice");
    }

    private static void EditingWhilePlacementWriteCompletes(Action<bool, string> assert)
    {
        using var fixture = new Fixture();
        var original = fixture.PlaceCompact(collapsed: true);
        Task first = fixture.Save();
        fixture.WaitForFirstWrite();
        fixture.EnlargeTemporaryEditor();
        fixture.ReleaseFirstWrite(first);
        fixture.DisposeManager();
        assert(fixture.Saved == original && fixture.Saved is { IsCollapsed: true } && fixture.SaveAttempts == 1,
            "SP16 shutdown during a temporarily resized editor persists the original collapsed placement after a pending layout write");
        fixture.Dispatch(first);
        assert(fixture.Saved == original && fixture.SaveAttempts == 1,
            "SP17 a delayed placement continuation cannot replace normal bounds with disposed editor geometry");
    }

    private static void Complete(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("Presentation upgrade did not finish.");
        task.GetAwaiter().GetResult();
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
            ((System.Collections.IDictionary)_manager.GetType().GetField("_forms", Private)!.GetValue(_manager)!).Add(_id, _form);
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

        internal StickerLayout PlaceCompact(bool collapsed)
        {
            float scale = _form.DeviceDpi / 96F;
            _form.ClientSize = new Size((int)Math.Round(260 * scale), (int)Math.Round(36 * scale));
            _form.Location = new Point(80, 80);
            _form.GetType().GetMethod("ApplyPresentation")!.Invoke(_form, [collapsed, true]);
            Invoke("Remember", _form);
            return Field<Dictionary<Guid, StickerLayout>>("_layouts")[_id];
        }

        internal void EnlargeTemporaryEditor()
        {
            _form.GetType().GetMethod("BeginNoteEdit")!.Invoke(_form, [(Func<string, Task>)(_ => Task.CompletedTask)]);
            _form.Size += new Size(40, 30);
            Invoke("Remember", _form);
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
