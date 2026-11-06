using System.Reflection;
using WorkBookmark.App;
using WorkBookmark.Core;

namespace WorkBookmark.Desktop.Tests;

internal static class RecentLifetimeChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type RecentType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.UI.RecentForm")!;

    internal static void Run(Action<bool, string> assert)
    {
        Bookmark[] records = [Make("첫 번째 접근성 항목", 3), Make("두 번째 접근성 항목", 2), Make("세 번째 접근성 항목", 1)];
        var response = new TaskCompletionSource<SearchResults>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool delayRead = false;
        Func<string, Task<SearchResults>> load = _ => delayRead ? response.Task : Task.FromResult(new SearchResults(records, false));
        using var form = (Form)Activator.CreateInstance(RecentType, load)!;
        Pump((Task)RecentType.GetMethod("OpenAsync")!.Invoke(form, null)!);
        var list = (ListBox)RecentType.GetField("_items", Private)!.GetValue(form)!;
        list.SelectedIndex = 0;
        var provider = list.AccessibilityObject;
        assert(provider.GetChildCount() == records.Length && provider.GetChild(0) is not null && provider.GetSelected() is not null,
            "RL01 the live bookmark list retains its native accessible children and selection provider");

        delayRead = true;
        Task reload = (Task)RecentType.GetMethod("ReloadAsync")!.Invoke(form, [true])!;
        bool reentrantUpdatesIgnored = false;
        using var probe = new DestroyProbe(list, () =>
        {
            // UIA/COM provider disconnection can pump the UI thread. Complete a
            // queued read and publish a change at that same teardown boundary.
            response.SetResult(new SearchResults([.. records, Make("종료 중 도착한 항목", 4)], false));
            Pump(reload);
            RecentType.GetMethod("ApplyFolderChanges")!.Invoke(form, [new[] { Make("종료 중 도착한 폴더", 5) }]);
            reentrantUpdatesIgnored = list.Items.Count == 0;
        });
        form.Dispose();
        assert(probe.SawDestroy && probe.ItemCount == 0 && probe.AccessibleChildCount == 0 && !probe.UncachedChildWasCreated,
            "RL02 native list destruction starts after logical and accessible children have been cleared, so queries cannot repopulate the provider cache");
        assert(reentrantUpdatesIgnored && reload.IsCompletedSuccessfully && form.IsDisposed && list.IsDisposed,
            "RL03 a read completion and committed update reentering native destruction cannot refill the closing list");
        ReentrantClear(records, reopen: false, assert);
        ReentrantClear(records, reopen: true, assert);
    }

    private static void ReentrantClear(Bookmark[] records, bool reopen, Action<bool, string> assert)
    {
        Func<string, Task<SearchResults>> load = _ => Task.FromResult(new SearchResults(records, false));
        using var form = (Form)Activator.CreateInstance(RecentType, load)!;
        Pump((Task)RecentType.GetMethod("OpenAsync")!.Invoke(form, null)!);
        var list = (ListBox)RecentType.GetField("_items", Private)!.GetValue(form)!;
        _ = list.AccessibilityObject.GetChild(0);
        if (reopen) form.Hide();
        // A synchronous native/COM callback can close the form in the middle
        // of Clear(), before its calling OpenAsync/ProjectItems has returned.
        using var probe = new ResetProbe(list, form.Dispose);
        Task operation = reopen ? (Task)RecentType.GetMethod("OpenAsync")!.Invoke(form, null)!
            : (Task)RecentType.GetMethod("ReloadAsync")!.Invoke(form, [true])!;
        Pump(operation);
        assert(probe.Triggered && operation.IsCompletedSuccessfully && form.IsDisposed && list.IsDisposed && list.Items.Count == 0 && !form.IsHandleCreated,
            reopen ? "RL05 a native Clear callback disposing the form prevents OpenAsync from recreating or showing it"
                : "RL04 a native Clear callback disposing the form prevents the already-running projection from refilling it");
    }

    private sealed class ResetProbe : NativeWindow, IDisposable
    {
        private readonly Action _afterReset;
        internal bool Triggered { get; private set; }

        internal ResetProbe(ListBox list, Action afterReset) { _afterReset = afterReset; AssignHandle(list.Handle); }

        protected override void WndProc(ref Message message)
        {
            bool reenter = message.Msg == 0x0184 && !Triggered; // LB_RESETCONTENT
            if (reenter) Triggered = true;
            base.WndProc(ref message);
            if (reenter) _afterReset();
        }

        public void Dispose() { if (Handle != nint.Zero) ReleaseHandle(); }
    }

    private sealed class DestroyProbe : NativeWindow, IDisposable
    {
        private readonly ListBox _list;
        private readonly Action _reenter;
        internal bool SawDestroy { get; private set; }
        internal int ItemCount { get; private set; }
        internal int AccessibleChildCount { get; private set; }
        internal bool UncachedChildWasCreated { get; private set; }

        internal DestroyProbe(ListBox list, Action reenter)
        {
            _list = list;
            _reenter = reenter;
            AssignHandle(list.Handle);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0002 && !SawDestroy) // WM_DESTROY, before ListBox releases UIA providers.
            {
                SawDestroy = true;
                _reenter();
                ItemCount = _list.Items.Count;
                AccessibleChildCount = _list.AccessibilityObject.GetChildCount();
                UncachedChildWasCreated = _list.AccessibilityObject.GetChild(2) is not null;
            }
            base.WndProc(ref message);
        }

        public void Dispose() { if (Handle != nint.Zero) ReleaseHandle(); }
    }

    private static Bookmark Make(string name, long sequence) => new(Guid.NewGuid(),
        new CapturedTarget(TargetKind.File, @"C:\synthetic-list-lifetime\" + name + ".txt"), name, name, "",
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, sequence, null, null, null, null);

    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("List lifetime operation timed out.");
        task.GetAwaiter().GetResult();
    }
}
