using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using WorkBookmark.App;
using WorkBookmark.Core;

namespace WorkBookmark.Desktop.Tests;

internal static class StickerShowFocusChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type RecentType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.UI.RecentForm")!;
    private static readonly Type StickerType = typeof(BookmarkApplicationContext).Assembly.GetType("WorkBookmark.App.UI.StickerForm")!;

    internal static int NotRunCount { get; private set; }

    internal static void Run(Action<bool, string> assert, bool requireForeground = true)
    {
        NotRunCount = 0;
        string mode = requireForeground ? "NATIVE" : "SAME-THREAD COMPONENT";
        Console.WriteLine($"FOCUS MODE: {mode}; " + (requireForeground
            ? "requires actual system foreground and focused control"
            : "checks only thread-active window and focused control; not other-app or Alt+Tab acceptance"));
        var results = new List<(bool Passed, string Description)>();
        var sample = Sample();
        Func<string, Task<SearchResults>> load = _ => Task.FromResult(new SearchResults([sample], false));
        using var recent = (Form)Activator.CreateInstance(RecentType, load)!;
        using var sticker = (Form)Activator.CreateInstance(StickerType, sample)!;
        var search = (TextBox)RecentType.GetField("_search", Private)!.GetValue(recent)!;
        int recentDeactivations = 0, stickerActivations = 0;
        var events = new List<string>();
        recent.Deactivate += (_, _) =>
        {
            recentDeactivations++;
            events.Add("recent.Deactivate " + State(recent, search, sticker));
        };
        sticker.Activated += (_, _) =>
        {
            stickerActivations++;
            events.Add("sticker.Activated " + State(recent, search, sticker));
        };

        // Exercise first display and two subsequent displays through the real
        // WinForms/Win32 path. No Deactivate or focus event is invoked manually.
        for (int iteration = 1; iteration <= 3; iteration++)
        {
            string id = $"SF0{iteration}";
            sticker.Hide();
            Pump((Task)RecentType.GetMethod("OpenAsync")!.Invoke(recent, null)!);
            PumpMessages(100);
            if (!Ready(recent, search, requireForeground))
            {
                NotRun(id, $"{mode} could not establish the recent window/search prerequisite", State(recent, search, sticker));
                continue;
            }

            int deactivationsBefore = recentDeactivations;
            int activationsBefore = stickerActivations;
            events.Clear();
            string before = State(recent, search, sticker);
            nint foregroundBefore = GetForegroundWindow();
            sticker.Show();
            bool immediate = Ready(recent, search, requireForeground) && GetForegroundWindow() == foregroundBefore;
            string immediateState = State(recent, search, sticker);
            PumpMessages(100);
            bool passed = immediate && Ready(recent, search, requireForeground) && GetForegroundWindow() == foregroundBefore &&
                sticker.Visible && sticker.Enabled &&
                recentDeactivations == deactivationsBefore && stickerActivations == activationsBefore;
            Record(id, passed,
                $"sticker hidden-to-visible transition {iteration} keeps the recent list visible and its search focused without activation",
                before, immediateState, State(recent, search, sticker), events);
        }

        // A separate ordinary Form supplies a deterministic same-thread foreign
        // window. This is a focus regression check, not an Alt+Tab acceptance test.
        using var other = new Form
        {
            Text = "WorkBookmark synthetic focus target",
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(80, 80, 360, 160),
            ShowInTaskbar = false
        };
        using var otherText = new TextBox { Dock = DockStyle.Top, Text = "synthetic foreground control" };
        other.Controls.Add(otherText);
        for (int iteration = 1; iteration <= 2; iteration++)
        {
            string id = $"SF0{iteration + 3}";
            sticker.Hide();
            other.Show();
            other.Activate();
            otherText.Focus();
            PumpMessages(100);
            if (!Ready(other, otherText, requireForeground))
            {
                NotRun(id, $"{mode} could not establish the synthetic other window/control prerequisite", State(other, otherText, sticker));
                continue;
            }

            int activationsBefore = stickerActivations;
            string before = State(other, otherText, sticker);
            nint foregroundBefore = GetForegroundWindow();
            sticker.Show();
            bool immediate = Ready(other, otherText, requireForeground) && GetForegroundWindow() == foregroundBefore;
            string immediateState = State(other, otherText, sticker);
            PumpMessages(100);
            Record(id, immediate && Ready(other, otherText, requireForeground) && GetForegroundWindow() == foregroundBefore &&
                sticker.Visible && sticker.Enabled && stickerActivations == activationsBefore,
                "showing a sticker preserves the fixture window's activation and keyboard focus without changing system foreground",
                before, immediateState, State(other, otherText, sticker), []);
        }

        sticker.Hide();
        sticker.Enabled = false;
        other.Show();
        other.Activate();
        otherText.Focus();
        PumpMessages(100);
        if (!Ready(other, otherText, requireForeground))
        {
            NotRun("SF06", $"{mode} could not establish the active-window/focus prerequisite for a disabled sticker", State(other, otherText, sticker));
        }
        else
        {
            int activationsBefore = stickerActivations;
            string before = State(other, otherText, sticker);
            nint foregroundBefore = GetForegroundWindow();
            sticker.Show();
            bool immediate = Ready(other, otherText, requireForeground) && GetForegroundWindow() == foregroundBefore;
            string immediateState = State(other, otherText, sticker);
            PumpMessages(100);
            Record("SF06", immediate && Ready(other, otherText, requireForeground) && GetForegroundWindow() == foregroundBefore &&
                sticker.Visible && !sticker.Enabled && stickerActivations == activationsBefore,
                "showing an already disabled sticker preserves its disabled state and the fixture window's activation/focus",
                before, immediateState, State(other, otherText, sticker), []);
        }

        if (!requireForeground)
        {
            sticker.Hide();
            sticker.Enabled = true;
            StickerType.GetMethod("SetBusy")!.Invoke(sticker, [true]);
            other.Activate();
            otherText.Focus();
            sticker.Show();
            var resume = (Control)StickerType.GetField("_resume", Private)!.GetValue(sticker)!;
            results.Add((sticker.Visible && sticker.Enabled && !resume.Enabled && Ready(other, otherText, false),
                "SF07 SAME-THREAD COMPONENT: showing a busy sticker preserves its disabled action and the existing focus"));
            StickerType.GetMethod("SetBusy")!.Invoke(sticker, [false]);
            int saves = 0;
            StickerType.GetMethod("BeginNoteEdit")!.Invoke(sticker, [(Func<string, Task>)(_ => { saves++; return Task.CompletedTask; })]);
            var editor = (TextBox)StickerType.GetField("_noteEditor", Private)!.GetValue(sticker)!;
            PumpMessages(100);
            results.Add((Ready(sticker, editor, false) && resume.Enabled == false && saves == 0,
                "SF08 SAME-THREAD COMPONENT: explicit note editing activates and focuses the editor after the show guard is removed"));
        }

        // Gather every native observation before an assertion can stop the runner.
        // Dispose all fixture windows first so failures leave no test UI behind.
        sticker.Dispose();
        recent.Dispose();
        other.Dispose();
        foreach (var result in results) assert(result.Passed, result.Description);

        void Record(string id, bool passed, string description, string before, string immediate, string after, IReadOnlyList<string> observedEvents)
        {
            string evidence = $"{id} before=[{before}] immediate=[{immediate}] after=[{after}]";
            Console.WriteLine($"{mode} {(passed ? "PASS" : "FAIL")}: {evidence}");
            foreach (string observed in observedEvents) Console.WriteLine($"{mode} EVENT {id}: {observed}");
            results.Add((passed, $"{id} {mode}: {description}; {evidence}"));
        }
    }

    private static bool Ready(Form form, Control control, bool requireForeground) => form.Visible &&
        GetActiveWindow() == form.Handle && (!requireForeground || GetForegroundWindow() == form.Handle) &&
        GetFocus() == control.Handle && control.Focused;

    private static string State(Form expected, Control control, Form sticker) =>
        $"foreground={Hex(GetForegroundWindow())}, active={Hex(GetActiveWindow())}, focus={Hex(GetFocus())}, expected={Hex(expected.Handle)}, " +
        $"control={Hex(control.Handle)}, expectedVisible={expected.Visible}, controlFocused={control.Focused}, " +
        $"expectedTopMost={expected.TopMost}, sticker={(sticker.IsHandleCreated ? Hex(sticker.Handle) : "none")}, " +
        $"stickerVisible={sticker.Visible}, stickerEnabled={sticker.Enabled}, stickerTopMost={sticker.TopMost}";

    private static string Hex(nint value) => $"0x{value.ToInt64():X}";

    private static void NotRun(string id, string reason, string state)
    {
        NotRunCount++;
        Console.WriteLine($"NOT RUN: {id} {reason}; {state}");
    }

    private static Bookmark Sample()
    {
        var now = DateTimeOffset.UtcNow;
        var target = new CapturedTarget(TargetKind.File, @"C:\synthetic\sticker-focus.txt");
        return new(Guid.NewGuid(), target, target.Path, "Synthetic focus check", "", now, now, 1, null, null, null, null);
    }

    private static void Pump(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("Recent form opening timed out during native focus checks.");
        task.GetAwaiter().GetResult();
    }

    private static void PumpMessages(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        do { Application.DoEvents(); Thread.Sleep(1); } while (watch.ElapsedMilliseconds < milliseconds);
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetActiveWindow();
    [DllImport("user32.dll")] private static extern nint GetFocus();
}
