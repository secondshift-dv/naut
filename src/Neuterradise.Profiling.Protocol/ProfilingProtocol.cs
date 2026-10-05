using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neuterradise.Profiling.Protocol;

public static class ProfilingProtocolVersion
{
    public const int Current = 1;
    public const uint MaximumFramePayloadSize = 4 * 1024 * 1024;
}

public enum ProfilingMessageType
{
    Hello,
    HelloAck,
    Ping,
    Pong,
    AnalyzeFaces,
    AnalyzeFacesResult,
    ExtractStills,
    ExtractStillsResult,
    BuildIdentityIndex,
    BuildIdentityIndexResult,
    MatchIdentityCandidates,
    MatchIdentityCandidatesResult,
    CancelRequest,
    ReleaseIndex,
    Shutdown,
    Error
}

public sealed record ProfilingEnvelope
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; } = ProfilingProtocolVersion.Current;

    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = Guid.NewGuid().ToString("D");

    [JsonPropertyName("messageType")]
    public ProfilingMessageType MessageType { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }

    public static ProfilingEnvelope Create<T>(
        ProfilingMessageType messageType,
        T payload,
        string? requestId = null,
        int protocolVersion = ProfilingProtocolVersion.Current) =>
        new()
        {
            ProtocolVersion = protocolVersion,
            RequestId = requestId ?? Guid.NewGuid().ToString("D"),
            MessageType = messageType,
            Payload = JsonSerializer.SerializeToElement(payload, ProfilingProtocolSerializer.Options)
        };

    public static ProfilingEnvelope Create(
        ProfilingMessageType messageType,
        string? requestId = null,
        int protocolVersion = ProfilingProtocolVersion.Current) =>
        new()
        {
            ProtocolVersion = protocolVersion,
            RequestId = requestId ?? Guid.NewGuid().ToString("D"),
            MessageType = messageType
        };

    public T? DeserializePayload<T>() =>
        Payload is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }
            ? default
            : JsonSerializer.Deserialize<T>(Payload.Value.GetRawText(), ProfilingProtocolSerializer.Options);
}

public static class ProfilingProtocolSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static int MeasurePayloadSize(ProfilingEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateEnvelope(envelope);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, Options).Length;
    }

    public static byte[] SerializeFrame(ProfilingEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateEnvelope(envelope);
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
        if (payload.Length == 0)
        {
            throw new ProfilingProtocolException("Frame payload cannot be empty.");
        }

        if (payload.Length > ProfilingProtocolVersion.MaximumFramePayloadSize)
        {
            throw new ProfilingProtocolException(
                $"Frame payload size {payload.Length} exceeds maximum {ProfilingProtocolVersion.MaximumFramePayloadSize} bytes.");
        }

        var frame = new byte[sizeof(uint) + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(sizeof(uint)));
        return frame;
    }

    public static async Task WriteFrameAsync(
        Stream stream,
        ProfilingEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var frame = SerializeFrame(envelope);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ProfilingEnvelope?> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var lengthBuffer = new byte[sizeof(uint)];
        var lengthBytesRead = 0;
        while (lengthBytesRead < sizeof(uint))
        {
            var read = await stream.ReadAsync(
                lengthBuffer.AsMemory(lengthBytesRead, sizeof(uint) - lengthBytesRead),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (lengthBytesRead == 0)
                {
                    return null;
                }

                throw new ProfilingProtocolException("Unexpected end of stream while reading frame length.");
            }

            lengthBytesRead += read;
        }

        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthBuffer);
        if (payloadLength is 0 or > ProfilingProtocolVersion.MaximumFramePayloadSize)
        {
            throw new ProfilingProtocolException($"Invalid frame payload length {payloadLength}.");
        }

        var payload = new byte[payloadLength];
        var payloadBytesRead = 0;
        while (payloadBytesRead < payloadLength)
        {
            var read = await stream.ReadAsync(
                payload.AsMemory(payloadBytesRead, (int)payloadLength - payloadBytesRead),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new ProfilingProtocolException("Unexpected end of stream while reading frame payload.");
            }

            payloadBytesRead += read;
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<ProfilingEnvelope>(payload, Options);
            if (envelope is null)
            {
                throw new ProfilingProtocolException("Protocol envelope is invalid.");
            }

            ValidateEnvelope(envelope);
            return envelope;
        }
        catch (JsonException exception)
        {
            throw new ProfilingProtocolException("Protocol frame contains malformed JSON.", exception);
        }
    }

    private static void ValidateEnvelope(ProfilingEnvelope envelope)
    {
        if (envelope.ProtocolVersion != ProfilingProtocolVersion.Current)
            throw new ProfilingProtocolException($"Unsupported profiling protocol version {envelope.ProtocolVersion}.");
        if (!Guid.TryParse(envelope.RequestId, out _))
            throw new ProfilingProtocolException("Protocol requestId must be a GUID.");
        if (envelope.MessageType is ProfilingMessageType.Hello or ProfilingMessageType.HelloAck or ProfilingMessageType.Ping or ProfilingMessageType.Pong
            or ProfilingMessageType.CancelRequest or ProfilingMessageType.Shutdown or ProfilingMessageType.Error)
            return;
        if (envelope.Payload is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
            throw new ProfilingProtocolException($"Message {envelope.MessageType} requires a payload.");

        if (envelope.MessageType == ProfilingMessageType.ReleaseIndex)
        {
            var release = envelope.DeserializePayload<ReleaseIdentityIndexRequest>();
            if (release is null || string.IsNullOrWhiteSpace(release.EmbeddingSpaceKey))
            {
                throw new ProfilingProtocolException(
                    "ReleaseIndex requires a non-empty embedding space key.");
            }
        }
    }
}

public sealed class ProfilingProtocolException : InvalidOperationException
{
    public ProfilingProtocolException(string message) : base(message) { }
    public ProfilingProtocolException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed record HelloPayload(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("workerBuildVersion")] string WorkerBuildVersion,
    [property: JsonPropertyName("supportedRequestTypes")] IReadOnlyList<string> SupportedRequestTypes,
    [property: JsonPropertyName("yuNetModelAvailable")] bool YuNetModelAvailable,
    [property: JsonPropertyName("sFaceModelAvailable")] bool SFaceModelAvailable,
    [property: JsonPropertyName("supportedEmbeddingSpaces")] IReadOnlyList<string> SupportedEmbeddingSpaces);

public sealed record HelloAckPayload(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("acceptedCompatibility")] bool AcceptedCompatibility,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage = null);

public sealed record PingPayload([property: JsonPropertyName("timestampUtcMs")] long TimestampUtcMs);
public sealed record PongPayload([property: JsonPropertyName("timestampUtcMs")] long TimestampUtcMs);
public sealed record CancelRequestPayload([property: JsonPropertyName("targetRequestId")] string TargetRequestId);
public sealed record ShutdownPayload([property: JsonPropertyName("reason")] string? Reason = null);
public sealed record ErrorPayload(
    [property: JsonPropertyName("errorCode")] string ErrorCode,
    [property: JsonPropertyName("errorMessage")] string ErrorMessage,
    [property: JsonPropertyName("isFatal")] bool IsFatal);
