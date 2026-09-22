using System.Reflection;
using WorkBookmark.App;
using WorkBookmark.Core;
using WorkBookmark.Storage;

namespace WorkBookmark.Desktop.Tests;

internal static class ResumeNotificationChecks
{
    internal static void Run(string directory, Action<bool, string> assert)
    {
        Directory.CreateDirectory(directory);
        (UserSettings.Default with { CaptureHotkey = new Hotkey(7, (int)Keys.F13), RecentHotkey = new Hotkey(7, (int)Keys.F14), IntroShown = true }).Save(directory);
        using var repository = new SqliteBookmarkRepository(Path.Combine(directory, "resume.db"));
        using var worker = new WorkerClient();
        using var context = new BookmarkApplicationContext(repository, worker, directory);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(BookmarkApplicationContext);
        var notify = type.GetMethod("Notify", flags)!;
        var result = type.GetMethod("NotifyResumeResult", flags)!;
        var toast = type.GetField("_toast", flags)!;
        try
        {
            notify.Invoke(context, ["여는 중…", null, null, 3000]);
            var progress = (Form)toast.GetValue(context)!;
            result.Invoke(context, [ResultCode.ResumeOutcomeUnknown]);
            assert(progress.IsDisposed && toast.GetValue(context) is null,
                "unconfirmed resume quietly clears progress instead of displaying repetitive warning");
            result.Invoke(context, [ResultCode.TargetUnavailable]);
            assert(toast.GetValue(context) is Form { Visible: true }, "confirmed open failures remain visible");
            result.Invoke(context, [ResultCode.PositionRestoredFocusPending]);
            var confirmed = (Form)toast.GetValue(context)!;
            assert(confirmed.Controls.Cast<Control>().Any(control => control.Text.Contains("기록한 위치로 이동했습니다", StringComparison.Ordinal)),
                "verified position keeps success after focus changes");
        }
        finally { context.ExitThread(); }
    }
}
