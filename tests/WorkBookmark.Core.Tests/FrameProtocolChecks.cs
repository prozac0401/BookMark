using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using WorkBookmark.Core;

internal static class FrameProtocolChecks
{
    // Keep this oracle explicitly reflection-based even when production uses generated metadata.
    private static readonly JsonSerializerOptions LegacyOptions = new()
    {
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private static readonly Guid RequestId = Guid.Parse("504dd776-32bf-4aaf-9c85-1091311faf13");
    private static readonly DateTimeOffset Deadline = new DateTimeOffset(2026, 11, 4, 20, 0, 0, TimeSpan.FromHours(9)).AddTicks(1234567);

    internal static int Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task<int> RunAsync()
    {
        int passed = 0, failed = 0;
        async Task Check(string name, Func<Task> check)
        {
            try { await check(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
        }

        // The frame layer transports DTO fields; target-kind validation is a separate boundary.
        var target = new CapturedTarget(TargetKind.NotepadSnapshot, "snapshot:한글/😀", "시트", "$D$127", true,
            37, 257, 2, 4, "제목 & <tag>", 1, "한글😀\n\"quoted\"\\end", 3, "메모 제목");
        var snapshot = new TargetSnapshot(2147483650L, 123, 638979840001234567L, 2147483651L, uint.MaxValue, 2147483652L);
        var observation = new NotepadObservation("synthetic-fingerprint", 1, 3, true);
        var request = new WorkerRequest(1, RequestId, Operation.Resume, Deadline, snapshot, target, observation, true);
        var response = new WorkerResponse(1, RequestId, ResultCode.PositionRestored, target, true, 2147483652L, observation);
        var browserRequest = new BrowserCaptureRequest(1, "capture", RequestId,
            "https://example.invalid/Case/%ED%95%9C?q=a%2Bb&keep=YES#fragment-한글", "한글😀 · \"quoted\" & <tag>");
        var browserResponse = new BrowserCaptureResponse(RequestId, true, "CaptureCommitted", "저장됨😀 · \"ok\" & <tag>");

        await Check("FP01 WorkerRequest preserves exact legacy bytes and reciprocal decoding", () => VerifyWire(request));
        await Check("FP02 WorkerResponse preserves exact legacy bytes and reciprocal decoding", () => VerifyWire(response));
        await Check("FP03 BrowserCaptureRequest preserves URL spelling, escaping and property names", () => VerifyWire(browserRequest));
        await Check("FP04 BrowserCaptureResponse preserves exact legacy bytes and reciprocal decoding", () => VerifyWire(browserResponse));
        await Check("FP05 Null/default fields remain present and enums remain numeric", async () =>
        {
            var minimalRequest = new WorkerRequest(1, RequestId, Operation.Capture, Deadline);
            var minimalResponse = new WorkerResponse(1, RequestId, ResultCode.Captured);
            await VerifyWire(minimalRequest);
            await VerifyWire(minimalResponse);
            var payload = Encoding.UTF8.GetString(await Payload(minimalRequest));
            Assert(payload.Contains("\"Operation\":0", StringComparison.Ordinal), "Operation enum became a string.");
            Assert(payload.Contains("\"Snapshot\":null", StringComparison.Ordinal) &&
                payload.Contains("\"Target\":null", StringComparison.Ordinal) &&
                payload.Contains("\"MonitorInput\":false", StringComparison.Ordinal), "Default/null fields were omitted.");
        });
        await Check("FP06 Unregistered generic dictionary and string retain reflection fallback", async () =>
        {
            await VerifyWire(new Dictionary<string, string?> { ["한글😀"] = "A&B\n\"quoted\"", ["missing"] = null });
            await VerifyWire("generic fallback · 한글😀 & < > \\");
        });
        await Check("FP07 Worker property matching remains case-sensitive", async () =>
        {
            var json = JsonSerializer.Serialize(request, LegacyOptions).Replace("\"ProtocolVersion\":", "\"protocolVersion\":", StringComparison.Ordinal);
            var oldValue = JsonSerializer.Deserialize<WorkerRequest>(json, LegacyOptions)!;
            var newValue = await Read<WorkerRequest>(json);
            Assert(oldValue.ProtocolVersion == 0 && newValue == oldValue, "Incorrectly cased worker property became recognized.");
        });
        await Check("FP08 Browser unknown/wrong-case fields are rejected while worker extras remain allowed", async () =>
        {
            var browserJson = JsonSerializer.Serialize(browserRequest, LegacyOptions);
            foreach (var invalid in new[]
            {
                browserJson[..^1] + ",\"sheetName\":\"Sheet1\"}",
                browserJson.Replace("\"protocolVersion\":", "\"ProtocolVersion\":", StringComparison.Ordinal)
            })
            {
                RejectsJson(() => JsonSerializer.Deserialize<BrowserCaptureRequest>(invalid, LegacyOptions));
                await RejectsJsonAsync(() => Read<BrowserCaptureRequest>(invalid));
            }
            var workerJson = JsonSerializer.Serialize(request, LegacyOptions);
            var withExtra = workerJson[..^1] + ",\"FutureField\":\"ignored\"}";
            Assert(JsonSerializer.Deserialize<WorkerRequest>(withExtra, LegacyOptions) == request &&
                await Read<WorkerRequest>(withExtra) == request, "Worker unknown-field policy changed.");
        });
        await Check("FP09 Maximum JSON depth stays 16 on the generated worker route", async () =>
        {
            var root = JsonSerializer.Serialize(new WorkerRequest(1, RequestId, Operation.Capture, Deadline), LegacyOptions);
            string AtDepth(int depth) => root[..^1] + ",\"FutureField\":" + new string('[', depth - 1) + "0" + new string(']', depth - 1) + "}";
            string accepted = AtDepth(16), rejected = AtDepth(17);
            Assert(await Read<WorkerRequest>(accepted) == JsonSerializer.Deserialize<WorkerRequest>(accepted, LegacyOptions), "Depth 16 no longer decodes identically.");
            RejectsJson(() => JsonSerializer.Deserialize<WorkerRequest>(rejected, LegacyOptions));
            await RejectsJsonAsync(() => Read<WorkerRequest>(rejected));
        });
        await Check("FP10 Null messages remain invalid for generated and fallback readers", async () =>
        {
            await RejectsEmpty<WorkerRequest>();
            await RejectsEmpty<Dictionary<string, string>>();
        });

        Console.WriteLine($"FRAME PROTOCOL RESULT: {passed} passed; {failed} failed. Wire compatibility only; process-start latency is measured separately.");
        return failed == 0 ? 0 : 1;
    }

    private static async Task VerifyWire<T>(T value)
    {
        byte[] expected = JsonSerializer.SerializeToUtf8Bytes(value, LegacyOptions);
        using var stream = new MemoryStream();
        await FrameProtocol.WriteAsync(stream, value);
        byte[] actual = stream.ToArray();
        Assert(actual.Length == expected.Length + 4 && BinaryPrimitives.ReadInt32LittleEndian(actual) == expected.Length,
            "Frame length/header differs from the legacy payload.");
        Assert(actual.AsSpan(4).SequenceEqual(expected), "UTF-8 payload differs from reflection serialization.");
        var oldDecoded = JsonSerializer.Deserialize<T>(actual.AsSpan(4), LegacyOptions);
        Assert(JsonSerializer.SerializeToUtf8Bytes(oldDecoded, LegacyOptions).AsSpan().SequenceEqual(expected), "Legacy reader cannot decode generated output identically.");
        using var oldFrame = Frame(expected);
        var newDecoded = await FrameProtocol.ReadAsync<T>(oldFrame);
        Assert(JsonSerializer.SerializeToUtf8Bytes(newDecoded, LegacyOptions).AsSpan().SequenceEqual(expected), "New reader cannot decode legacy output identically.");
    }

    private static async Task<byte[]> Payload<T>(T value)
    {
        using var stream = new MemoryStream();
        await FrameProtocol.WriteAsync(stream, value);
        return stream.ToArray()[4..];
    }

    private static MemoryStream Frame(byte[] payload)
    {
        var bytes = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, payload.Length);
        payload.CopyTo(bytes, 4);
        return new MemoryStream(bytes);
    }

    private static async Task<T> Read<T>(string json)
    {
        using var frame = Frame(Encoding.UTF8.GetBytes(json));
        return await FrameProtocol.ReadAsync<T>(frame);
    }

    private static void RejectsJson(Action read)
    {
        try { read(); }
        catch (JsonException) { return; }
        throw new InvalidOperationException("Legacy decoder accepted invalid JSON.");
    }

    private static async Task RejectsJsonAsync<T>(Func<Task<T>> read)
    {
        try { await read(); }
        catch (JsonException) { return; }
        throw new InvalidOperationException("Generated decoder accepted invalid JSON.");
    }

    private static async Task RejectsEmpty<T>()
    {
        try { await Read<T>("null"); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Null message was accepted.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
