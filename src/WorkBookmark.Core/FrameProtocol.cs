using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace WorkBookmark.Core;

/// <summary>Length-prefixed, bounded DTO-only pipe protocol. Never put metadata on a command line.</summary>
public static class FrameProtocol
{
    public const int Version = 1;
    // A maximum-size UTF-16 snapshot may expand to 12 MiB when JSON escapes each character.
    public const int MaximumFrameBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 16, PropertyNameCaseInsensitive = false };

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken = default)
    {
        JsonTypeInfo<T>? generated = GeneratedTypeInfo<T>();
        byte[] payload = generated is null
            ? JsonSerializer.SerializeToUtf8Bytes(message, Options)
            : JsonSerializer.SerializeToUtf8Bytes(message, generated);
        if (payload.Length is 0 or > MaximumFrameBytes) throw new InvalidDataException("Invalid frame size.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrameBytes) throw new InvalidDataException("Invalid frame size.");
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        JsonTypeInfo<T>? generated = GeneratedTypeInfo<T>();
        return (generated is null
            ? JsonSerializer.Deserialize<T>(payload, Options)
            : JsonSerializer.Deserialize(payload, generated)) ?? throw new InvalidDataException("Empty message.");
    }

    private static JsonTypeInfo<T>? GeneratedTypeInfo<T>()
    {
        // Each worker is a fresh process. Avoid rediscovering these fixed wire
        // contracts through reflection, while preserving the generic API fallback.
        if (typeof(T) == typeof(WorkerRequest)) return (JsonTypeInfo<T>)(object)FrameProtocolJsonContext.Default.WorkerRequest;
        if (typeof(T) == typeof(WorkerResponse)) return (JsonTypeInfo<T>)(object)FrameProtocolJsonContext.Default.WorkerResponse;
        if (typeof(T) == typeof(BrowserCaptureRequest)) return (JsonTypeInfo<T>)(object)FrameProtocolJsonContext.Default.BrowserCaptureRequest;
        if (typeof(T) == typeof(BrowserCaptureResponse)) return (JsonTypeInfo<T>)(object)FrameProtocolJsonContext.Default.BrowserCaptureResponse;
        return null;
    }

    public static bool IsValid(WorkerRequest request, DateTimeOffset now) =>
        request.ProtocolVersion == Version && request.RequestId != Guid.Empty && Enum.IsDefined(request.Operation) &&
        request.Target?.Kind != TargetKind.BookmarkFolder &&
        (!request.MonitorInput || request.Operation == Operation.Resume && request.Target is not null) &&
        request.DeadlineUtc > now && request.DeadlineUtc <= now.AddSeconds(ResumeTimeoutSeconds(request.Operation, request.Target) + 1) &&
        (request.Operation == Operation.Capture ? request.Snapshot is { Hwnd: not 0, ProcessId: > 0 } : request.Target is not null);

    // Cold Office startup and integrated sign-in need longer than local file navigation.
    public static int ResumeTimeoutSeconds(Operation operation, CapturedTarget? target) =>
        operation == Operation.Resume && target is not null && OfficeLocation.IsWebTarget(target) ? 45 : 15;
}
