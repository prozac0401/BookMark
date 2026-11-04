using System.Diagnostics;
using System.Buffers.Binary;
using WorkBookmark.Core;
using WorkBookmark.App;

internal static class Program
{
    private static int passed;
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--fixture")) return await Fixture(args);
        if (args.Length == 3 && args[0] == "--production-worker") return await ProductionWorkerStartupChecks.RunAsync(args[1], args[2]);
        string exe = Environment.ProcessPath!;
        var results = new List<object>();
        var responses = new List<object>();
        var fixtureTraces = new List<string>();
        string? traceDirectory = args.SkipWhile(a => a != "--trace-directory").Skip(1).FirstOrDefault();
        if (traceDirectory is not null) Directory.CreateDirectory(traceDirectory);
        async Task Check(string id, Func<Task> run)
        {
            responses.Clear(); fixtureTraces.Clear();
            var startedUtc = DateTimeOffset.UtcNow;
            var watch = Stopwatch.StartNew();
            try { await run(); passed++; results.Add(new { test = id, status = "PASS", startedUtc, completedUtc = DateTimeOffset.UtcNow, milliseconds = watch.ElapsedMilliseconds, responses = responses.ToArray(), fixtureTraces = fixtureTraces.ToArray() }); Console.WriteLine($"PASS {id}"); }
            catch (Exception e) { results.Add(new { test = id, status = "FAIL", startedUtc, completedUtc = DateTimeOffset.UtcNow, milliseconds = watch.ElapsedMilliseconds, error = e.Message, responses = responses.ToArray(), fixtureTraces = fixtureTraces.ToArray() }); Console.WriteLine($"FAIL {id}: {e.Message}"); }
        }
        WorkerRequest Request(Operation op = Operation.Capture, double seconds = 3) => new(1, Guid.NewGuid(), op, DateTimeOffset.UtcNow.AddSeconds(seconds), new(1, 1), new(TargetKind.File, @"C:\fixture\report.txt"));
        WorkerClient Client(string mode, params string[] fixtureArguments)
        {
            var arguments = new List<string> { "--fixture", mode };
            arguments.AddRange(fixtureArguments);
            if (traceDirectory is not null)
            {
                string trace = Path.Combine(traceDirectory, $"{results.Count:D2}-{mode}-{Guid.NewGuid():N}.tsv");
                fixtureTraces.Add(trace);
                arguments.AddRange(["--trace", trace]);
            }
            return new(exe, arguments);
        }
        async Task<WorkerResponse> Observe(WorkerClient client, WorkerRequest request)
        {
            var dispatchedUtc = DateTimeOffset.UtcNow;
            var watch = Stopwatch.StartNew();
            var response = await client.RunAsync(request);
            responses.Add(new { dispatchedUtc, deadlineUtc = request.DeadlineUtc, milliseconds = watch.ElapsedMilliseconds,
                code = response.Code.ToString(), requestIdMatches = response.RequestId == request.RequestId,
                targetPresent = response.Target is not null, targetMatches = response.Target == request.Target });
            return response;
        }
        await Check("D11 protocol-roundtrip", async () =>
        {
            using var stream = new MemoryStream(); var request = Request();
            await FrameProtocol.WriteAsync(stream, request); stream.Position = 0;
            Assert(await FrameProtocol.ReadAsync<WorkerRequest>(stream) == request, "DTO changed.");
        });
        await Check("D11 all-resumes-share-input-without-cancelling-open", async () =>
        {
            foreach (var kind in Enum.GetValues<TargetKind>().Where(kind => kind != TargetKind.BookmarkFolder))
            {
                var request = Request(Operation.Resume) with { MonitorInput = true, Target = new(kind, @"C:\fixture\report.txt") };
                Assert(FrameProtocol.IsValid(request, DateTimeOffset.UtcNow), "Input signal rejected for " + kind);
                using var stream = new MemoryStream();
                await FrameProtocol.WriteAsync(stream, request); stream.Position = 0;
                Assert(await FrameProtocol.ReadAsync<WorkerRequest>(stream) == request, "Input monitoring lost for " + kind);
            }
            Assert(!FrameProtocol.IsValid(Request() with { MonitorInput = true }, DateTimeOffset.UtcNow), "Resume monitor accepted for capture.");
            Assert(!FrameProtocol.IsValid(Request(Operation.ValidateRelink) with { MonitorInput = true }, DateTimeOffset.UtcNow), "Resume monitor accepted for relink validation.");
            Assert(!FrameProtocol.IsValid(Request(Operation.Resume) with { Target = new(TargetKind.BookmarkFolder, "") }, DateTimeOffset.UtcNow), "Linkless folder accepted as an external resume target.");
        });
        await Check("D11 oversized-and-truncated-frames", async () =>
        {
            foreach (int n in new[] { -1, 0, FrameProtocol.MaximumFrameBytes + 1 })
            {
                byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, n);
                try { await FrameProtocol.ReadAsync<WorkerRequest>(new MemoryStream(header)); throw new Exception("Accepted invalid frame."); }
                catch (InvalidDataException) { }
            }
            try { await FrameProtocol.ReadAsync<WorkerRequest>(new MemoryStream([5,0,0,0,123])); throw new Exception("Accepted truncated frame."); }
            catch (EndOfStreamException) { }
        });
        await Check("D04 valid-result-id", async () =>
        {
            using var client = Client("ok"); var request = Request();
            var result = await Observe(client, request);
            Assert(result.RequestId == request.RequestId && result.Code == ResultCode.Captured && result.Target is not null, $"Valid DTO not accepted: {result.Code}; target present: {result.Target is not null}.");
        });
        await Check("D04 wrong-request-id-rejected", async () =>
        {
            using var client = Client("wrong-id");
            var result = await Observe(client, Request());
            Assert(result.Code == ResultCode.InvalidRequest, $"Expected InvalidRequest for mismatched response ID; actual: {result.Code}.");
        });
        await Check("D04 capture-deadline-no-late-success", async () =>
        {
            using var client = Client("late"); var watch = Stopwatch.StartNew();
            var result = await client.RunAsync(Request(seconds: .5));
            Assert(result.Code == ResultCode.CaptureTimedOut && result.Target is null, "Late capture accepted.");
            Assert(watch.Elapsed < TimeSpan.FromSeconds(2) && !client.IsBusy, "Deadline/cleanup failed.");
        });
        await Check("D05 resume-deadline-outcome-unknown", async () =>
        {
            using var client = Client("late");
            var result = await client.RunAsync(Request(Operation.Resume, .5));
            Assert(result.Code == ResultCode.ResumeOutcomeUnknown && result.ExternalActionStarted, "Unsafe cancellation claim.");
        });
        await Check("O01 web-Office-capture-worker-result", async () =>
        {
            using var client = Client("echo-target");
            var target = new CapturedTarget(TargetKind.WordPosition, "https://office.invalid/Shared Documents/한글.docx", HadUnsavedChanges: false, WordStart: 37);
            var result = await Observe(client, Request() with { Target = target });
            Assert(result.Code == ResultCode.Captured && result.Target == target, $"Expected Captured with exact Office metadata; actual: {result.Code}; target matches: {result.Target == target}.");
        });
        await Check("O02 web-Office-timeout-never-claims-position-restored", async () =>
        {
            using var client = Client("late");
            var target = new CapturedTarget(TargetKind.ExcelCell, "https://office.invalid/document.xlsx", "Sheet1", "$D$127", false);
            var result = await client.RunAsync(Request(Operation.Resume, .5) with { Target = target });
            Assert(result.Code == ResultCode.OfficeResumePending && result.ExternalActionStarted && !client.IsBusy, "Remote timeout must invite sign-in/retry without claiming success.");
        });
        await Check("O03 web-Office-cancellation-never-claims-position-restored", async () =>
        {
            using var client = Client("late"); using var cancel = new CancellationTokenSource(150);
            var target = new CapturedTarget(TargetKind.PowerPointSlide, "https://office.invalid/document.pptx", HadUnsavedChanges: false, SlideId: 257, SlideNumber: 2);
            var result = await client.RunAsync(Request(Operation.Resume) with { Target = target }, cancel.Token);
            Assert(result.Code == ResultCode.OfficeResumePending && !client.IsBusy, "Sign-in input/cancellation must not produce a successful resume.");
        });
        await Check("O04 extended-deadline-only-for-web-Office-resume", async () =>
        {
            var target = new CapturedTarget(TargetKind.WordPosition, "https://office.invalid/document.docx", HadUnsavedChanges: false, WordStart: 37);
            using var client = Client("document-opened");
            var request = Request(Operation.Resume, 45) with { Target = target };
            Assert(FrameProtocol.IsValid(request, DateTimeOffset.UtcNow), "Cold web Office startup deadline rejected.");
            var result = await client.RunAsync(request);
            Assert(result.Code == ResultCode.OfficeDocumentOpened && result.TargetHwnd == 123, "Confirmed document must remain distinct from position restoration.");
            Assert((await client.RunAsync(request with { Operation = Operation.Capture })).Code == ResultCode.InvalidRequest, "Long capture deadline accepted.");
            Assert((await client.RunAsync(request with { Target = new(TargetKind.File, @"C:\fixture\report.txt") })).Code == ResultCode.InvalidRequest, "Long local deadline accepted.");
            Assert((await client.RunAsync(request with { DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(60) })).Code == ResultCode.InvalidRequest, "Unbounded remote deadline accepted.");
        });
        await Check("U12 single-flight-no-queue", async () =>
        {
            using var client = Client("late"); var first = client.RunAsync(Request(seconds: .6));
            var second = await client.RunAsync(Request());
            Assert(second.Code == ResultCode.AppBusy, "Concurrent external operation accepted.");
            await first;
        });
        await Check("D04 cancellation-discards-response", async () =>
        {
            using var client = Client("late"); using var cts = new CancellationTokenSource(150);
            var result = await client.RunAsync(Request(), cts.Token);
            Assert(result.Code == ResultCode.CaptureTimedOut && result.Target is null, "Cancelled capture accepted.");
        });
        await Check("D06 worker-crash-cleanup", async () =>
        {
            using var client = Client("crash"); var result = await client.RunAsync(Request());
            Assert(result.Code == ResultCode.InvalidRequest && !client.IsBusy, "Worker failure not contained.");
        });
        await Check("D06 worker-child-survives-timeout", async () =>
        {
            string marker = Path.Combine(Path.GetTempPath(), $"WorkBookmark-test-{Guid.NewGuid():N}.pid");
            using var client = Client("spawn-child", marker);
            Process? child = null;
            try
            {
                // This fixture starts two .NET processes before publishing readiness;
                // use the normal capture allowance, while still requiring a real timeout.
                var result = await client.RunAsync(Request());
                Assert(result.Code == ResultCode.CaptureTimedOut, "Expected timeout.");
                Assert(File.Exists(marker), "Child fixture did not start.");
                child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker)));
                Assert(!child.HasExited, "Descendant was incorrectly terminated.");
            }
            finally
            {
                if (child is not null)
                {
                    try
                    {
                        if (!child.HasExited) child.Kill(false);
                        Assert(child.WaitForExit(1500), "Child fixture did not exit during cleanup.");
                    }
                    finally { child.Dispose(); }
                }
                await DeleteFixtureMarkersAsync(marker);
            }
        });
        await Check("D06 dispose-terminates-worker-synchronously", async () =>
        {
            string marker = Path.Combine(Path.GetTempPath(), $"WorkBookmark-worker-{Guid.NewGuid():N}.pid");
            using var client = Client("own-pid", marker);
            Process? worker = null;
            Task<WorkerResponse>? pending = null;
            try
            {
                var readiness = Stopwatch.StartNew();
                pending = client.RunAsync(Request(seconds: 4));
                // Readiness is a setup prerequisite, not a one-second product SLA.
                // Leave a full second of the fixture request for the Dispose assertion.
                while (!File.Exists(marker) && !pending.IsCompleted && readiness.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(25);
                Assert(File.Exists(marker), $"Worker fixture was not ready before Dispose: elapsed={readiness.ElapsedMilliseconds}ms; request completed={pending.IsCompleted}.");
                worker = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker)));
                client.Dispose();
                Assert(worker.WaitForExit(1500), "Worker survived Dispose.");
                Assert((await pending).Code != ResultCode.Captured, "Disposed request succeeded.");
                Assert((await client.RunAsync(Request())).Code == ResultCode.InvalidRequest, "Disposed client restarted.");
            }
            finally
            {
                client.Dispose();
                if (pending is not null) await pending.WaitAsync(TimeSpan.FromSeconds(2));
                worker?.Dispose();
                await DeleteFixtureMarkersAsync(marker);
            }
        });
        await Check("D04 deadline-boundary-does-not-stick-busy", async () =>
        {
            using var client = Client("late");
            for (int i = 0; i < 5; i++)
            {
                var result = await client.RunAsync(Request(seconds: .001));
                Assert(result.Code is ResultCode.InvalidRequest or ResultCode.CaptureTimedOut, "Unexpected near-deadline result.");
                Assert(!client.IsBusy, "Near-deadline request left client busy.");
            }
        });
        string? output = args.SkipWhile(a => a != "--output").Skip(1).FirstOrDefault();
        if (output is not null) await File.WriteAllTextAsync(output, System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{passed}/{results.Count} passed");
        return passed == results.Count ? 0 : 1;
    }
    private static async Task<int> Fixture(string[] args)
    {
        string mode = args[1];
        bool traceEnabled = Array.IndexOf(args, "--trace") >= 0;
        if (traceEnabled) TraceFixture(args, "Entered");
        if (mode == "child") { await Task.Delay(30000); return 0; }
        if (mode == "crash") return 17;
        var request = await FrameProtocol.ReadAsync<WorkerRequest>(Console.OpenStandardInput());
        if (traceEnabled) TraceFixture(args, "RequestRead", request);
        if (mode == "spawn-child")
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--fixture"); info.ArgumentList.Add("child");
            using var child = Process.Start(info)!; await PublishPidAsync(args[2], child.Id);
            await Task.Delay(10000);
        }
        if (mode == "own-pid") { await PublishPidAsync(args[2], Environment.ProcessId); await Task.Delay(10000); }
        if (mode == "late") await Task.Delay(5000);
        var response = new WorkerResponse(1, mode == "wrong-id" ? Guid.NewGuid() : request.RequestId,
            mode == "document-opened" ? ResultCode.OfficeDocumentOpened : ResultCode.Captured,
            mode == "echo-target" ? request.Target : new(TargetKind.File, @"C:\fixture\report.txt"), TargetHwnd: mode == "document-opened" ? 123 : 0);
        if (traceEnabled) TraceFixture(args, "ResponseReady", request);
        await FrameProtocol.WriteAsync(Console.OpenStandardOutput(), response); return 0;
    }
    private static void TraceFixture(string[] args, string stage, WorkerRequest? request = null)
    {
        string? trace = args.SkipWhile(a => a != "--trace").Skip(1).FirstOrDefault();
        if (trace is null) return;
        var observedUtc = DateTimeOffset.UtcNow;
        using var process = Process.GetCurrentProcess();
        // Optional test-only diagnostics contain no document metadata and never use stdout.
        // Keep tracing cheap: JSON reflection metadata here would perturb cold startup.
        File.AppendAllText(trace, FormattableString.Invariant($"{stage}\t{observedUtc:O}\t{process.Id}\t{process.StartTime.ToUniversalTime():O}\t{request?.RequestId}\t{request?.DeadlineUtc:O}\t{(request is null ? (double?)null : (request.DeadlineUtc - observedUtc).TotalMilliseconds)}") + Environment.NewLine);
    }
    private static async Task PublishPidAsync(string marker, int processId)
    {
        // Publish readiness only after WriteAllTextAsync has closed its stream. File.Exists on
        // the final marker must never race with the fixture writing the PID it announces.
        string pending = marker + ".pending";
        await File.WriteAllTextAsync(pending, processId.ToString());
        File.Move(pending, marker);
    }
    private static async Task DeleteFixtureMarkersAsync(string marker)
    {
        // Kill requests termination but does not wait for Windows to release the
        // fixture's pending PID stream. Only sharing/lock violations merit a bounded retry.
        var cleanup = Stopwatch.StartNew();
        while (true)
        {
            try { File.Delete(marker + ".pending"); File.Delete(marker); return; }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && cleanup.Elapsed < TimeSpan.FromSeconds(2))
            { await Task.Delay(25); }
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
