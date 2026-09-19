using System.IO.Pipes;
using WorkBookmark.Browser;
using WorkBookmark.Core;

// Native Messaging uses stdout exclusively for one framed response. Never log metadata there.
internal static class Program
{
 private static async Task<int> Main()
 {
  Guid id = Guid.Empty;
  BrowserCaptureResponse response;
  using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
  try
  {
   var request = await FrameProtocol.ReadAsync<BrowserCaptureRequest>(Console.OpenStandardInput(), deadline.Token);
   id = request.RequestId;
   BrowserProtocol.Validate(request);
   using var pipe = new NamedPipeClientStream(".", BrowserPipeName.Value, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
   try { await pipe.ConnectAsync(2000, deadline.Token); }
   catch (TimeoutException) { return await Respond(BrowserProtocol.Failure(id, "AppNotRunning", "업무 책갈피 앱을 먼저 실행해 주세요.")); }
   await FrameProtocol.WriteAsync(pipe, request, deadline.Token);
   response = await FrameProtocol.ReadAsync<BrowserCaptureResponse>(pipe, deadline.Token);
   if (response.RequestId != id || response.Success != (response.Code == "CaptureCommitted"))
    response = BrowserProtocol.Failure(id, "InvalidResponse", "저장 결과를 확인하지 못했습니다. 최근 목록을 확인해 주세요.");
  }
  catch (BookmarkException e) { response = BrowserProtocol.Failure(id, e.Code.ToString(), "이 주소는 지원하지 않습니다. HTTP 또는 HTTPS 페이지에서 사용해 주세요."); }
  catch (OperationCanceledException) { response = BrowserProtocol.Failure(id, "OutcomeUnknown", "저장 결과를 확인하지 못했습니다. 최근 목록을 확인해 주세요."); }
  catch { response = BrowserProtocol.Failure(id, "InvalidRequest", "연결 또는 메시지를 확인하지 못했습니다. 최근 목록을 확인해 주세요."); }
  return await Respond(response);
 }
 private static async Task<int> Respond(BrowserCaptureResponse response)
 {
  try
  {
   using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
   await FrameProtocol.WriteAsync(Console.OpenStandardOutput(), response, timeout.Token);
   return 0;
  }
  catch { return 1; }
 }
}
