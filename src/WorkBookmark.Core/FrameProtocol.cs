using System.Buffers.Binary;
using System.Text.Json;

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
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, Options);
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
        return JsonSerializer.Deserialize<T>(payload, Options) ?? throw new InvalidDataException("Empty message.");
    }

    public static bool IsValid(WorkerRequest request, DateTimeOffset now) =>
        request.ProtocolVersion == Version && request.RequestId != Guid.Empty && Enum.IsDefined(request.Operation) &&
        request.DeadlineUtc > now && request.DeadlineUtc <= now.AddSeconds(16) &&
        (request.Operation == Operation.Capture ? request.Snapshot is { Hwnd: not 0, ProcessId: > 0 } : request.Target is not null);
}
