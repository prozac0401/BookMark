using System.IO.Pipes;
using WorkBookmark.Browser;
using WorkBookmark.Core;
namespace WorkBookmark.App;

/// <summary>One bounded local request at a time; same Windows user and interactive session only.</summary>
internal sealed class BrowserCaptureServer : IDisposable
{
 private readonly CancellationTokenSource _shutdown = new();
 private readonly Func<BrowserCaptureRequest, CancellationToken, Task<BrowserCaptureResponse>> _capture;
 internal BrowserCaptureServer(Func<BrowserCaptureRequest, CancellationToken, Task<BrowserCaptureResponse>> capture)
 { _capture = capture; _ = ListenAsync(); }
 private async Task ListenAsync()
 {
  while (!_shutdown.IsCancellationRequested)
  {
   try
   {
    using var pipe = new NamedPipeServerStream(BrowserPipeName.Value, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.WaitForConnectionAsync(_shutdown.Token);
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
    timeout.CancelAfter(TimeSpan.FromSeconds(8));
    Guid id = Guid.Empty;
    BrowserCaptureResponse response;
    try
    {
     var request = await FrameProtocol.ReadAsync<BrowserCaptureRequest>(pipe, timeout.Token);
     id = request.RequestId;
     BrowserProtocol.Validate(request);
     response = await _capture(request, timeout.Token);
    }
    catch (BookmarkException e) { response = BrowserProtocol.Failure(id, e.Code.ToString(), "지원하지 않는 페이지입니다."); }
    catch (OperationCanceledException) { response = BrowserProtocol.Failure(id, "OutcomeUnknown", "저장 결과를 확인하지 못했습니다. 최근 목록을 확인해 주세요."); }
    catch { response = BrowserProtocol.Failure(id, "InvalidRequest", "요청을 처리하지 못했습니다."); }
    await FrameProtocol.WriteAsync(pipe, response, timeout.Token);
   }
   catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
   catch { if (!_shutdown.IsCancellationRequested) await Task.Delay(100, CancellationToken.None); }
  }
 }
 public void Dispose() { _shutdown.Cancel(); }
}
