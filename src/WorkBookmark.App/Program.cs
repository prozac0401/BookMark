using System.Security.Principal;
using WorkBookmark.Storage;

namespace WorkBookmark.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Must precede single-instance enforcement: workers are deliberately separate processes.
        if (args.Length == 1 && args[0] == "--worker") return WorkerHost.Run();
        if (args.Length == 1 && args[0] == "--startup-worker") return StartupRegistration.RunWorker();
        string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        string instanceName = @"Local\WorkBookmark_" + user;
        if (args.Length == 1 && args[0] == "--installer-shutdown") return InstallerLifetime.RequestShutdown(instanceName);
        // Publish the shutdown signal before acquiring the instance mutex so MSI cannot miss it at startup.
        using var installerShutdown = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + "_Shutdown");
        using var mutex = new Mutex(true, instanceName, out bool firstInstance);
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + "_ShowRecent");
        if (!firstInstance) { signal.Set(); return 0; }
        string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkBookmark");
        try
        {
            Directory.CreateDirectory(dataDirectory);
            DiagnosticLog.Initialize(dataDirectory);
            using var repository = new SqliteBookmarkRepository(Path.Combine(dataDirectory, "bookmarks.db"));
            using var worker = new WorkerClient();
            using var context = new BookmarkApplicationContext(repository, worker, dataDirectory);
            using var dispatch = new Control();
            dispatch.CreateControl();
            using var shutdown = new ManualResetEvent(false);
            _ = Task.Run(() =>
            {
                WaitHandle[] handles = [shutdown, signal, installerShutdown];
                int requested;
                while ((requested = WaitHandle.WaitAny(handles)) != 0)
                {
                    try
                    {
                        if (requested == 2) { dispatch.BeginInvoke(() => context.ExitThread()); break; }
                        dispatch.BeginInvoke(() => context.ShowBookmarks());
                    }
                    catch (InvalidOperationException) { break; }
                }
            });
            Application.Run(context);
            shutdown.Set();
            worker.Cancel();
            return 0;
        }
        catch (Exception)
        {
            DiagnosticLog.Write("Startup", Core.ResultCode.PersistenceFailed);
            MessageBox.Show($"기록 또는 설정을 읽을 수 없어 시작하지 못했습니다. 원본 데이터를 초기화하지 않았습니다.\n\n데이터 폴더: {dataDirectory}\n문제 해결 전에 이 폴더를 보존해 주세요.", "업무 책갈피 · 시작 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally { mutex.ReleaseMutex(); }
    }
}
