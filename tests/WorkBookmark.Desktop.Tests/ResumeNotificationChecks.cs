using System.Reflection;
using System.Drawing;
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
        var tray = (NotifyIcon)type.GetField("_tray", flags)!.GetValue(context)!;
        var recent = (Form)type.GetField("_recent", flags)!.GetValue(context)!;
        try
        {
            assert(tray.Visible && tray.Icon is not null && tray.Icon.Handle != SystemIcons.Application.Handle,
                "notification area uses the product icon rather than the generic Windows application icon");
            assert(recent.Icon is not null && recent.Icon.Handle != SystemIcons.Application.Handle,
                "application forms use the product icon");
            foreach (var quietResult in new[] { ResultCode.ResumeOutcomeUnknown, ResultCode.OfficeDocumentOpened })
            {
                notify.Invoke(context, ["여는 중…", null, null, 3000]);
                var progress = (Form)toast.GetValue(context)!;
                result.Invoke(context, [quietResult, null]);
                assert(progress.IsDisposed && toast.GetValue(context) is null,
                    $"{quietResult} quietly clears progress instead of displaying repetitive warning");
                result.Invoke(context, [quietResult, null]);
                assert(toast.GetValue(context) is null, $"{quietResult} remains quiet without an active progress toast");
            }
            foreach (var visibleResult in new[] { ResultCode.TargetUnavailable, ResultCode.OpenedPositionFailed, ResultCode.OfficeResumePending })
            {
                result.Invoke(context, [visibleResult, null]);
                assert(toast.GetValue(context) is Form { Visible: true }, $"{visibleResult} remains visible for user action");
            }
            var folder = repository.UpsertCapture(new CapturedTarget(TargetKind.Folder, directory)).Bookmark;
            repository.RecordResume(folder.Id, ResultCode.TargetUnavailable);
            result.Invoke(context, [ResultCode.TargetUnavailable, repository.Get(folder.Id)]);
            var failedFolder = (Form)toast.GetValue(context)!;
            assert(failedFolder.Controls.OfType<LinkLabel>().Single().Text == "위치 다시 지정…",
                "a failed filesystem folder can be repaired directly from its notification");
            var page = repository.UpsertCapture(new CapturedTarget(TargetKind.WebPage, "https://example.org/old", PageTitle: "Old page")).Bookmark;
            result.Invoke(context, [ResultCode.TargetUnavailable, page]);
            assert(((Form)toast.GetValue(context)!).Controls.OfType<LinkLabel>().Single().Text == "웹 주소 수정…",
                "a rejected browser launch offers a bookmark address editor");
            result.Invoke(context, [ResultCode.OpenRequested, page]);
            assert(!((Form)toast.GetValue(context)!).Controls.OfType<LinkLabel>().Any(),
                "a dispatched browser launch does not produce a broken-page warning");
            result.Invoke(context, [ResultCode.PositionRestoredFocusPending, null]);
            var confirmed = (Form)toast.GetValue(context)!;
            assert(confirmed.Controls.Cast<Control>().Any(control => control.Text.Contains("기록한 위치로 이동했습니다", StringComparison.Ordinal)),
                "verified position keeps success after focus changes");
            context.Dispose();
            assert(!tray.Visible && recent.IsDisposed && confirmed.IsDisposed,
                "disposing the application context removes the tray icon and closes owned forms");
        }
        finally { context.ExitThread(); }
    }
}
