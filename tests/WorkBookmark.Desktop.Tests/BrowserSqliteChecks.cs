using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using WorkBookmark.App;
using WorkBookmark.Core;
using WorkBookmark.Storage;

namespace WorkBookmark.Desktop.Tests;

// Real native host -> production pipe/server -> UI dispatcher -> temporary SQLite.
// No browser, Office document, production database, registry or startup entry is changed.
internal static class BrowserSqliteChecks
{
    private static int _passed;
    private static readonly Stopwatch Suite = new();

    internal static int Run(string nativeHost)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Suite.Restart();
        string root = Path.Combine(Path.GetTempPath(), "WorkBookmark-browser-sqlite-" + Guid.NewGuid().ToString("N"));
        string database = Path.Combine(root, "bookmarks.db");
        string user = WindowsIdentity.GetCurrent().User!.Value;
        string pipeName = "WorkBookmark_Browser_" + user + "_" + Process.GetCurrentProcess().SessionId;
        string host = Path.GetFullPath(nativeHost);
        using var mutex = new Mutex(true, @"Local\WorkBookmark_" + user, out bool ownsMutex);
        if (!ownsMutex) { Console.Error.WriteLine("BLOCKED: Exit WorkBookmark before running the isolated browser/SQLite checks."); return 2; }
        using var commitReached = new ManualResetEventSlim();
        using var allowCommit = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var watchdog = new System.Threading.Timer(_ =>
        {
            Console.Error.WriteLine("FAIL: browser/SQLite checks exceeded the 35-second suite deadline.");
            Environment.Exit(1);
        }, null, 35000, Timeout.Infinite);
        int captureMode = 0;
        BookmarkApplicationContext? context = null;
        SqliteBookmarkRepository? repository = null;
        WorkerClient? worker = null;
        try
        {
            if (!File.Exists(host)) throw new FileNotFoundException("Build the native host before this suite.", host);
            if (PipeState(pipeName) != 0) throw new InvalidOperationException("Production browser pipe exists; no test server was started.");
            Directory.CreateDirectory(root);
            (UserSettings.Default with
            {
                CaptureHotkey = new Hotkey(7, (int)Keys.F17),
                RecentHotkey = new Hotkey(7, (int)Keys.F18), IntroShown = true
            }).Save(root);
            repository = new SqliteBookmarkRepository(database, operation =>
            {
                if (operation != "capture") return;
                if (Volatile.Read(ref captureMode) == 1)
                {
                    commitReached.Set();
                    if (!allowCommit.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic commit gate was not released.");
                }
                if (Volatile.Read(ref captureMode) == 2) throw new IOException("Synthetic pre-commit failure.");
            });
            worker = new WorkerClient();
            context = new BookmarkApplicationContext(repository, worker, root);
            PumpUntil(() => PipeState(pipeName) == 1, "real app pipe becomes available");

            const string url = "https://example.invalid/Case/%2fpart?x=1&x=2#Section%202";
            var firstRequest = Request(url, "합성 브라우저 제목");
            Volatile.Write(ref captureMode, 1);
            Task<BrowserCaptureResponse> pending = HostRequest(host, firstRequest, deadline.Token);
            PumpUntil(() => commitReached.IsSet, "real SQLite reaches the pre-commit gate");
            PumpFor(150);
            Check(!pending.IsCompleted, "native host cannot acknowledge before the real SQLite transaction commits");
            Check(CountRows(database) == 0, "independent SQLite connection cannot see the uncommitted bookmark");
            Volatile.Write(ref captureMode, 0);
            allowCommit.Set();
            var firstReply = Finish(pending);
            Check(IsCommit(firstReply, firstRequest), "real native host receives the correlated commit acknowledgment");
            var first = repository.List().Items.Single();
            Check(ReadText(database, "SELECT path FROM bookmarks") == url &&
                ReadText(database, "SELECT page_title FROM bookmarks") == firstRequest.Title && first.Target.Kind == TargetKind.WebPage,
                "separate SQLite reads confirm durable exact URL and Unicode title");

            repository.UpdateNote(first.Id, "보존할 실제 DB 메모");
            var noted = repository.Get(first.Id)!;
            var recapture = Request(url, "변경된 제목");
            var recaptured = Finish(HostRequest(host, recapture, deadline.Token));
            var same = repository.List().Items.Single();
            Check(IsCommit(recaptured, recapture) && same.Id == first.Id && same.Note == noted.Note &&
                same.NoteUpdatedAtUtc == noted.NoteUpdatedAtUtc && same.CreatedAtUtc == first.CreatedAtUtc &&
                same.CaptureSequence > first.CaptureSequence && same.Target.PageTitle == recapture.Title,
                "native recapture updates title and sequence while preserving identity and note");

            var fragment = Request(url.Replace("Section%202", "Section%203", StringComparison.Ordinal), "다른 fragment");
            Check(IsCommit(Finish(HostRequest(host, fragment, deadline.Token)), fragment) && CountRows(database) == 2,
                "different URL fragment produces a separate committed bookmark");
            var query = Request(url.Replace("x=1&x=2", "x=2&x=1", StringComparison.Ordinal), "다른 query 순서");
            Check(IsCommit(Finish(HostRequest(host, query, deadline.Token)), query) && CountRows(database) == 3,
                "different query ordering produces a separate committed bookmark");

            long beforeFailureSequence = ReadNumber(database, "SELECT value FROM metadata WHERE name='capture_sequence'");
            var beforeFailure = repository.Get(first.Id)!;
            Volatile.Write(ref captureMode, 2);
            var failedRequest = Request(url, "롤백되어야 하는 제목");
            var failed = Finish(HostRequest(host, failedRequest, deadline.Token));
            Volatile.Write(ref captureMode, 0);
            Check(!failed.Success && failed.RequestId == failedRequest.RequestId && failed.Code == "PersistenceFailed",
                "native host reports the real SQLite pre-commit failure without success");
            Check(repository.Get(first.Id) == beforeFailure && CountRows(database) == 3 &&
                ReadNumber(database, "SELECT value FROM metadata WHERE name='capture_sequence'") == beforeFailureSequence,
                "failed real transaction rolls back title, note, row count and capture sequence");

            var invalid = Request("file:///C:/synthetic.txt", "지원하지 않는 주소");
            var invalidReply = Finish(HostRequest(host, invalid, deadline.Token));
            Check(!invalidReply.Success && invalidReply.RequestId == invalid.RequestId && invalidReply.Code == "UnsupportedTarget",
                "unsupported URI receives a negative native host response");
            Check(CountRows(database) == 3 && ReadNumber(database, "SELECT value FROM metadata WHERE name='capture_sequence'") == beforeFailureSequence,
                "rejected URI leaves the real database unchanged");
            var recovered = Request("https://example.invalid/after-failure", "정상 복구");
            Check(IsCommit(Finish(HostRequest(host, recovered, deadline.Token)), recovered) && CountRows(database) == 4 &&
                ReadNumber(database, "SELECT value FROM metadata WHERE name='capture_sequence'") == beforeFailureSequence + 1,
                "server accepts the next explicit request after rollback and invalid input");

            PumpUntil(() => PipeState(pipeName) == 1, "server is listening before shutdown case");
            var closingRequest = Request("https://example.invalid/must-not-commit-on-exit", "종료 직전 요청");
            Task<BrowserCaptureResponse> closing = HostRequest(host, closingRequest, deadline.Token);
            // Do not dispatch the queued UI commit until the application context is exiting.
            WaitWithoutPumping(() => PipeState(pipeName) == 2 || closing.IsCompleted, "native host connects before app exit");
            if (closing.IsCompleted) throw new InvalidOperationException("Shutdown request completed before the test observed a connected pipe.");
            context.ExitThread();
            var closingReply = Finish(closing);
            Check(!closingReply.Success && closingReply.Code != "CaptureCommitted",
                "connected native host fails within its deadline when the application exits");
            Check(CountRows(database) == 4 && ReadNumber(database, "SELECT value FROM metadata WHERE name='capture_sequence'") == beforeFailureSequence + 1,
                "queued request cannot commit after application exit");
            PumpUntil(() => PipeState(pipeName) == 0, "disposed app releases its browser pipe");
            Check(PipeState(pipeName) == 0, "application shutdown leaves no listening production pipe");
            Console.WriteLine($"RESULT: {_passed} browser/SQLite integration checks passed in {Suite.ElapsedMilliseconds} ms. Real native host, server, UI dispatch and temporary SQLite; installed browser UI remains separate.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            return 1;
        }
        finally
        {
            allowCommit.Set();
            context?.ExitThread();
            context?.Dispose();
            worker?.Dispose();
            deadline.Cancel();
            repository?.Dispose();
            // Let cancellation release the asynchronous listener before relinquishing the app mutex.
            var cleanup = Stopwatch.StartNew();
            while (PipeState(pipeName) != 0 && cleanup.Elapsed < TimeSpan.FromSeconds(2)) { Application.DoEvents(); Thread.Sleep(5); }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true); // This invocation's fresh temp tree only.
            mutex.ReleaseMutex();
        }
    }

    private static BrowserCaptureRequest Request(string url, string title) => new(1, "capture", Guid.NewGuid(), url, title);
    private static bool IsCommit(BrowserCaptureResponse reply, BrowserCaptureRequest request) =>
        reply.Success && reply.Code == "CaptureCommitted" && reply.RequestId == request.RequestId;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _passed++; Console.WriteLine("PASS: " + name);
    }
    private static BrowserCaptureResponse Finish(Task<BrowserCaptureResponse> pending)
    {
        PumpUntil(() => pending.IsCompleted, "native host reply");
        return pending.GetAwaiter().GetResult();
    }
    private static void PumpUntil(Func<bool> completed, string phase)
    {
        var watch = Stopwatch.StartNew();
        while (!completed())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(6) || Suite.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException(phase);
            Application.DoEvents(); Thread.Sleep(1);
        }
        Application.DoEvents();
    }
    private static void WaitWithoutPumping(Func<bool> completed, string phase)
    {
        var watch = Stopwatch.StartNew();
        while (!completed())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException(phase);
            Thread.Sleep(1);
        }
    }
    private static void PumpFor(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(1); }
    }
    private static async Task<BrowserCaptureResponse> HostRequest(string executable, BrowserCaptureRequest request, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var process = Process.Start(start) ?? throw new IOException("Native host did not start.");
        Task<string> error = process.StandardError.ReadToEndAsync(token);
        try
        {
            await FrameProtocol.WriteAsync(process.StandardInput.BaseStream, request, token).ConfigureAwait(false);
            process.StandardInput.Close();
            var response = await FrameProtocol.ReadAsync<BrowserCaptureResponse>(process.StandardOutput.BaseStream, token).ConfigureAwait(false);
            byte[] extra = new byte[1];
            if (await process.StandardOutput.BaseStream.ReadAsync(extra, token).ConfigureAwait(false) != 0) throw new InvalidDataException("Native stdout contains more than one frame.");
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            if (process.ExitCode != 0 || (await error.ConfigureAwait(false)).Length != 0) throw new IOException("Native host failed or emitted unexpected diagnostics.");
            return response;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: false); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        }
    }
    private static long CountRows(string database) => ReadNumber(database, "SELECT count(*) FROM bookmarks");
    private static long ReadNumber(string database, string sql) => Convert.ToInt64(ReadValue(database, sql));
    private static string ReadText(string database, string sql) => Convert.ToString(ReadValue(database, sql))!;
    private static object ReadValue(string database, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
        connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return command.ExecuteScalar() ?? throw new InvalidDataException("Expected SQLite scalar is absent.");
    }
    // 0: absent; 1: waiting listener; 2: connected/full. This never connects to another app.
    private static int PipeState(string name)
    {
        if (WaitNamedPipeW(@"\\.\pipe\" + name, 1)) return 1;
        int error = Marshal.GetLastWin32Error();
        if (error is 2 or 3) return 0;
        if (error is 121 or 231) return 2;
        throw new Win32Exception(error, "Cannot establish local browser pipe state.");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WaitNamedPipeW(string name, uint timeout);
}
