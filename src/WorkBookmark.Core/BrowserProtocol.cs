using System.Text.Json.Serialization;
namespace WorkBookmark.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BrowserCaptureRequest(
 [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
 [property: JsonPropertyName("action")] string Action,
 [property: JsonPropertyName("requestId")] Guid RequestId,
 [property: JsonPropertyName("url")] string Url,
 [property: JsonPropertyName("title")] string Title);
public sealed record BrowserCaptureResponse(
 [property: JsonPropertyName("requestId")] Guid RequestId,
 [property: JsonPropertyName("success")] bool Success,
 [property: JsonPropertyName("code")] string Code,
 [property: JsonPropertyName("message")] string Message);

/// <summary>Browser metadata only. No page content, credentials, or arbitrary URI handlers.</summary>
public static class BrowserProtocol
{
 public const int MaximumUrlLength = 16384;
 public static CapturedTarget Validate(BrowserCaptureRequest request)
 {
  if (request.ProtocolVersion != 1 || request.Action != "capture" || request.RequestId == Guid.Empty)
   throw new BookmarkException(ResultCode.InvalidRequest);
  return ValidateTarget(new CapturedTarget(TargetKind.WebPage, request.Url, PageTitle: request.Title));
 }
 public static CapturedTarget ValidateTarget(CapturedTarget target)
 {
  if (target.Kind != TargetKind.WebPage || target.Path is null || target.Path.Length is 0 or > MaximumUrlLength ||
      target.Path.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '\u007f') ||
      !Uri.TryCreate(target.Path, UriKind.Absolute, out var uri) ||
      (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(uri.Host) ||
      !string.IsNullOrEmpty(uri.UserInfo) || target.Path.Contains('\\') ||
      target.PageTitle is null || target.PageTitle.Length > 256 || target.PageTitle.Any(char.IsControl) ||
      target.SheetName is not null || target.CellAddress is not null || target.HadUnsavedChanges is not null ||
      target.WordStart is not null || target.SlideId is not null || target.SlideNumber is not null || target.PdfPage is not null || target.TextOffset is not null ||
      target.TextContent is not null || target.TextSelectionEnd is not null || target.SnapshotTitle is not null)
   throw new BookmarkException(ResultCode.UnsupportedTarget);
  // Preserve the exact URL: case, escaping, query, and fragment can affect application state.
  return target with { PageTitle = string.IsNullOrWhiteSpace(target.PageTitle) ? uri.Host : target.PageTitle };
 }
 public static BrowserCaptureResponse Failure(Guid id, string code, string message) => new(id, false, code, message);
}
