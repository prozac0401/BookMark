using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WorkBookmark.Browser;
using WorkBookmark.Core;

internal static class Program
{
    private static int passed;
    private static string host = "";
    private static CancellationToken deadline;
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || !File.Exists(args[0]) || !Path.GetFileName(args[0]).Equals("WorkBookmark.BrowserHost.exe", StringComparison.OrdinalIgnoreCase))
        { Console.Error.WriteLine("Usage: WorkBookmark.Browser.Tests PATH_TO_BUILT_WorkBookmark.BrowserHost.exe"); return 2; }
        host = Path.GetFullPath(args[0]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); deadline = timeout.Token;
        var elapsed = Stopwatch.StartNew();
        try
        {
            AssertPipeAbsent();
            var request = new BrowserCaptureRequest(1, "capture", Guid.NewGuid(), "https://example.com/Case/%ED%95%9C?q=a%2Bb&keep=YES#fragment-한글", "문서 제목 · a & b");
            BrowserCaptureRequest? forwarded = null;
            var response = await RunHost(Frame(request), async pipe =>
            {
                forwarded = await FrameProtocol.ReadAsync<BrowserCaptureRequest>(pipe, deadline);
                await FrameProtocol.WriteAsync(pipe, new BrowserCaptureResponse(forwarded.RequestId, true, "CaptureCommitted", "synthetic commit acknowledged"), deadline);
            });
            Assert(response.Success && response.Code == "CaptureCommitted" && response.RequestId == request.RequestId && forwarded == request,
                "real native host forwards exact title, case, URL escaping, query and fragment to same-user/session pipe");
            Assert(BrowserProtocol.Validate(request).Path == request.Url, "core preserves exact browser URL state");
            Assert(BrowserProtocol.Validate(request with { Title = "" }).PageTitle == "example.com", "blank title uses host without discarding URL");

            foreach (var (name, invalid, code) in new (string, BrowserCaptureRequest, string)[]
            {
                ("non-HTTP URI", request with { Url = "file:///C:/secret.txt" }, "UnsupportedTarget"),
                ("URI credentials", request with { Url = "https://user:password@example.com/" }, "UnsupportedTarget"),
                ("invalid protocol", request with { ProtocolVersion = 2 }, "InvalidRequest"),
                ("invalid action", request with { Action = "resume" }, "InvalidRequest"),
                ("missing request ID", request with { RequestId = Guid.Empty }, "InvalidRequest"),
                ("control character in title", request with { Title = "line1\nline2" }, "UnsupportedTarget"),
                ("oversized URL", request with { Url = "https://example.com/" + new string('a', BrowserProtocol.MaximumUrlLength) }, "UnsupportedTarget")
            })
            {
                response = await RunHost(Frame(invalid));
                Assert(!response.Success && response.Code == code && response.RequestId == invalid.RequestId, "host rejects " + name + " before contacting app");
            }
            foreach (var target in new[]
            {
                BrowserProtocol.Validate(request) with { SheetName = "Sheet1" },
                BrowserProtocol.Validate(request) with { WordStart = 0 },
                BrowserProtocol.Validate(request) with { SlideId = 256 },
                BrowserProtocol.Validate(request) with { PdfPage = 1 },
                BrowserProtocol.Validate(request) with { HadUnsavedChanges = false }
            })
            {
                bool refused = false;
                try { BrowserProtocol.ValidateTarget(target); } catch (BookmarkException) { refused = true; }
                Assert(refused, "core rejects document coordinate cross-fields " + passed);
            }
            var crossField = JsonSerializer.Serialize(request).TrimEnd('}') + ",\"sheetName\":\"Sheet1\"}";
            response = await RunHost(RawFrame(Encoding.UTF8.GetBytes(crossField)));
            Assert(!response.Success && response.Code == "InvalidRequest", "native request rejects unexpected coordinate fields");

            foreach (var (name, reply) in new (string, BrowserCaptureResponse)[]
            {
                ("mismatched response ID", new(Guid.NewGuid(), true, "CaptureCommitted", "wrong ID")),
                ("success flag with failure code", new(request.RequestId, true, "PersistenceFailed", "wrong flag")),
                ("failure flag with commit code", new(request.RequestId, false, "CaptureCommitted", "wrong flag"))
            })
            {
                response = await RunHost(Frame(request), async pipe =>
                {
                    _ = await FrameProtocol.ReadAsync<BrowserCaptureRequest>(pipe, deadline);
                    await FrameProtocol.WriteAsync(pipe, reply, deadline);
                });
                Assert(!response.Success && response.Code == "InvalidResponse" && response.RequestId == request.RequestId, "host rejects " + name);
            }
            response = await RunHost(Frame(request), async pipe =>
            {
                _ = await FrameProtocol.ReadAsync<BrowserCaptureRequest>(pipe, deadline);
                await FrameProtocol.WriteAsync(pipe, BrowserProtocol.Failure(request.RequestId, "AppBusy", "busy"), deadline);
            });
            Assert(!response.Success && response.Code == "AppBusy", "valid application failure is relayed without success");

            foreach (var (name, bytes) in new (string, byte[])[]
            {
                ("oversized native frame", Header(FrameProtocol.MaximumFrameBytes + 1)),
                ("truncated native frame", Header(100).Concat(new byte[] { (byte)'{' }).ToArray()),
                ("truncated native header", new byte[] { 1, 0 }),
                ("invalid UTF-8 JSON", RawFrame(new byte[] { 0xff, 0xfe })),
                ("null JSON request", RawFrame(Encoding.UTF8.GetBytes("null")))
            })
            {
                response = await RunHost(bytes);
                Assert(!response.Success && response.Code == "InvalidRequest", "host rejects " + name);
            }
            response = await RunHost(Frame(request), async pipe =>
            {
                _ = await FrameProtocol.ReadAsync<BrowserCaptureRequest>(pipe, deadline);
                await pipe.WriteAsync(Header(FrameProtocol.MaximumFrameBytes + 1), deadline);
                await pipe.FlushAsync(deadline);
            });
            Assert(!response.Success && response.Code == "InvalidRequest", "oversized app response cannot report a commit");

            // No mock and an absent named pipe: exercise production host's bounded 2 s connect timeout.
            AssertPipeAbsent();
            response = await RunHost(Frame(request));
            Assert(!response.Success && response.Code == "AppNotRunning" && response.RequestId == request.RequestId,
                "absent application pipe reports AppNotRunning within host connect deadline");
            Console.WriteLine($"RESULT: {passed} browser checks passed in {elapsed.ElapsedMilliseconds} ms. Real BrowserHost process + private mock pipe; no GUI, extension loading or actual database commit claimed.");
            return 0;
        }
        catch (Exception error)
        { Console.Error.WriteLine("FAIL: " + error); return 1; }
    }

    private static async Task<BrowserCaptureResponse> RunHost(byte[] input, Func<NamedPipeServerStream, Task>? serve = null)
    {
        AssertPipeAbsent();
        using var server = serve is null ? null : new NamedPipeServerStream(BrowserPipeName.Value, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var local = CancellationTokenSource.CreateLinkedTokenSource(deadline); local.CancelAfter(TimeSpan.FromSeconds(4));
        var token = local.Token;
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Host did not start.");
        Task serverTask = server is null ? Task.CompletedTask : Serve();
        async Task Serve() { await server!.WaitForConnectionAsync(token); await serve!(server); }
        try
        {
            var stderr = child.StandardError.ReadToEndAsync(token);
            await child.StandardInput.BaseStream.WriteAsync(input, token);
            await child.StandardInput.BaseStream.FlushAsync(token);
            child.StandardInput.Close();
            var reply = await FrameProtocol.ReadAsync<BrowserCaptureResponse>(child.StandardOutput.BaseStream, token);
            await child.WaitForExitAsync(token);
            await serverTask;
            var remainder = await child.StandardOutput.ReadToEndAsync(token);
            if (child.ExitCode != 0 || remainder.Length != 0 || (await stderr).Length != 0)
                throw new InvalidOperationException("Host protocol contaminated stdout/stderr or returned a failing exit code.");
            return reply;
        }
        finally
        {
            local.Cancel();
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(CancellationToken.None); }
            try { await serverTask; } catch when (local.IsCancellationRequested) { }
        }
    }
    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        passed++; Console.WriteLine("PASS: " + message);
    }
    private static byte[] Frame<T>(T message) => RawFrame(JsonSerializer.SerializeToUtf8Bytes(message));
    private static byte[] RawFrame(byte[] body) => Header(body.Length).Concat(body).ToArray();
    private static byte[] Header(int size) { var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, size); return header; }
    private static void AssertPipeAbsent()
    {
        // WaitNamedPipe observes availability without connecting to or sending data to a user's app.
        var present = WaitNamedPipeW(@"\\.\pipe\" + BrowserPipeName.Value, 1);
        var error = Marshal.GetLastWin32Error();
        if (present || error is 121 or 231) throw new InvalidOperationException("The real browser capture pipe is already in use. Exit WorkBookmark before running this isolated test.");
        if (error is not (2 or 3)) throw new Win32Exception(error, "Could not establish that the production browser pipe is absent.");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WaitNamedPipeW(string name, uint timeout);
}
