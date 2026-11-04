using System.Text.Json.Serialization;

namespace WorkBookmark.Core;

// Match FrameProtocol's existing JSON options and DTO attributes exactly.
// The default generation mode provides metadata for reads and fast-path writes.
[JsonSourceGenerationOptions(MaxDepth = 16, PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(WorkerRequest))]
[JsonSerializable(typeof(WorkerResponse))]
[JsonSerializable(typeof(BrowserCaptureRequest))]
[JsonSerializable(typeof(BrowserCaptureResponse))]
internal partial class FrameProtocolJsonContext : JsonSerializerContext { }
