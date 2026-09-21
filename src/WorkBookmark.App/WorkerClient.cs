using System.Diagnostics;
using WorkBookmark.Core;

namespace WorkBookmark.App;

/// <summary>One request, one isolated worker, one absolute deadline. No queued external actions.</summary>
public sealed class WorkerClient : IDisposable
{
    private int busy;
    private readonly object gate = new();
    private CancellationTokenSource? activeCancellation;
    private Process? activeProcess;
    private bool disposed;
    private readonly string executable;
    private readonly IReadOnlyList<string> prefixArguments;
    public bool IsBusy => Volatile.Read(ref busy) != 0;

    public WorkerClient(string? executablePath = null, IReadOnlyList<string>? arguments = null)
    {
        executable = executablePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable.");
        prefixArguments = arguments ?? [];
    }

    public async Task<WorkerResponse> RunAsync(WorkerRequest request, CancellationToken cancellationToken = default)
    {
        WorkerResponse Result(ResultCode code, bool unknown = false) => new(FrameProtocol.Version, request.RequestId,
            code == ResultCode.ResumeOutcomeUnknown && request.Operation == Operation.Resume && request.Target is { } target && OfficeLocation.IsWebTarget(target)
                ? ResultCode.OfficeResumePending : code, ExternalActionStarted: unknown);
        if (!FrameProtocol.IsValid(request, DateTimeOffset.UtcNow)) return Result(ResultCode.InvalidRequest);
        lock (gate) { if (disposed) return Result(ResultCode.InvalidRequest); }
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return Result(ResultCode.AppBusy);
        var watch = Stopwatch.StartNew();
        Process? process = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (gate) activeCancellation = deadline;

        try
        {
            TimeSpan remaining = request.DeadlineUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new OperationCanceledException();
            deadline.CancelAfter(remaining);
            deadline.Token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in prefixArguments) start.ArgumentList.Add(argument);
            start.ArgumentList.Add("--worker");
            lock (gate)
            {
                if (disposed) throw new OperationCanceledException();
                deadline.Token.ThrowIfCancellationRequested();
                process = Process.Start(start) ?? throw new InvalidOperationException("Worker did not start.");
                activeProcess = process;
            }
            // Drain without retaining arbitrary child output or recording it in diagnostic logs.
            Task errorDrain = DrainAsync(process.StandardError.BaseStream, deadline.Token);
            await FrameProtocol.WriteAsync(process.StandardInput.BaseStream, request, deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            WorkerResponse response = await FrameProtocol.ReadAsync<WorkerResponse>(process.StandardOutput.BaseStream, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= request.DeadlineUtc) throw new OperationCanceledException();
            if (response.ProtocolVersion != FrameProtocol.Version || response.RequestId != request.RequestId || !Enum.IsDefined(response.Code))
                return Result(request.Operation == Operation.Capture ? ResultCode.InvalidRequest : ResultCode.ResumeOutcomeUnknown, request.Operation != Operation.Capture);
            if (request.Operation == Operation.Capture && response.Code == ResultCode.Captured)
            {
                if (response.Target is null) return Result(ResultCode.InvalidRequest);
                _ = PathPolicy.Validate(response.Target);
            }
            DiagnosticLog.Write(request.Operation.ToString(), response.Code, watch.ElapsedMilliseconds);
            return response;
        }
        catch (OperationCanceledException)
        {
            ResultCode code = request.Operation == Operation.Capture ? ResultCode.CaptureTimedOut : ResultCode.ResumeOutcomeUnknown;
            DiagnosticLog.Write(request.Operation.ToString(), code, watch.ElapsedMilliseconds);
            return Result(code, request.Operation != Operation.Capture);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or BookmarkException)
        {
            ResultCode code = request.Operation == Operation.Capture ? ResultCode.InvalidRequest : ResultCode.ResumeOutcomeUnknown;
            DiagnosticLog.Write(request.Operation.ToString(), code, watch.ElapsedMilliseconds);
            return Result(code, request.Operation != Operation.Capture);
        }
        finally
        {
            // Only our own worker: never kill its process tree (Shell-launched documents may be descendants).
            lock (gate)
            {
                if (process is not null)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: false); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                    process.Dispose();
                }
                if (ReferenceEquals(activeProcess, process)) activeProcess = null;
            }
            lock (gate) { if (ReferenceEquals(activeCancellation, deadline)) activeCancellation = null; }
            Interlocked.Exchange(ref busy, 0);
        }
    }

    private static async Task DrainAsync(Stream source, CancellationToken token)
    {
        byte[] buffer = new byte[4096];
        try { while (await source.ReadAsync(buffer, token) > 0) { } }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    public void Cancel() { lock (gate) activeCancellation?.Cancel(); }
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            activeCancellation?.Cancel();
            // Cleanup must not depend on a WinForms continuation after its message loop stops.
            try { if (activeProcess is { HasExited: false }) activeProcess.Kill(entireProcessTree: false); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
}
