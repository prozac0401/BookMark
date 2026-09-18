using WorkBookmark.Core;
using WorkBookmark.Windows;

namespace WorkBookmark.App;

internal static class WorkerHost
{
    // Main is STA. Work enters on the WinForms pump and COM references never cross the pipe.
    public static int Run()
    {
        using var context = new ApplicationContext();
        using var dispatch = new Control();
        dispatch.CreateControl();
        int exitCode = 1;
        _ = Task.Run(async () =>
        {
            WorkerRequest request;
            try
            {
                using var readDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                request = await FrameProtocol.ReadAsync<WorkerRequest>(Console.OpenStandardInput(), readDeadline.Token);
                if (!FrameProtocol.IsValid(request, DateTimeOffset.UtcNow)) throw new InvalidDataException();
            }
            catch { dispatch.BeginInvoke(() => context.ExitThread()); return; }
            dispatch.BeginInvoke(async () =>
            {
                WorkerResponse result;
                try { result = WindowsAdapter.Execute(request); }
                catch (BookmarkException ex) { result = new(FrameProtocol.Version, request.RequestId, ex.Code); }
                catch { result = new(FrameProtocol.Version, request.RequestId, request.Operation == Operation.Capture ? ResultCode.UnsupportedTarget : ResultCode.ResumeOutcomeUnknown); }
                try
                {
                    await FrameProtocol.WriteAsync(Console.OpenStandardOutput(), result);
                    exitCode = 0;
                }
                catch { exitCode = 2; }
                context.ExitThread();
            });
        });
        Application.Run(context);
        return exitCode;
    }
}
