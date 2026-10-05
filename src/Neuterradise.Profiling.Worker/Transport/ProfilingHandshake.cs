using Neuterradise.Profiling.Protocol;
using System.Reflection;
namespace Neuterradise.Profiling.Worker.Transport;

using Neuterradise.Profiling.Worker.FaceAnalysis;

public static class ProfilingHandshake
{
    public static async Task<HelloAckPayload> PerformClientHandshakeAsync(
        ProfilingClientTransport client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var helloPayload = new HelloPayload(
            ProtocolVersion: ProfilingProtocolVersion.Current,
            WorkerBuildVersion: typeof(ProfilingHandshake).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(ProfilingHandshake).Assembly.GetName().Version?.ToString()
                ?? "unknown",
            SupportedRequestTypes:
            [
                "Ping",
                "CancelRequest",
                "Shutdown",
                "AnalyzeFaces",
                "ExtractStills",
                "BuildIdentityIndex",
                "MatchIdentityCandidates",
                "ReleaseIndex",
            ],
            YuNetModelAvailable: IsModelAvailable(
                YuNetFaceDetector.ModelId,
                YuNetFaceDetector.ArtifactFileName,
                YuNetFaceDetector.Sha256,
                "YuNet face detector"),
            SFaceModelAvailable: IsModelAvailable(
                SFaceEmbeddingExtractor.ModelId,
                SFaceEmbeddingExtractor.ArtifactFileName,
                SFaceEmbeddingExtractor.Sha256,
                "SFace face recognizer"),
            SupportedEmbeddingSpaces: [SFaceEmbeddingExtractor.BaselineEmbeddingSpaceKey]);

        var helloEnvelope = ProfilingEnvelope.Create(ProfilingMessageType.Hello, helloPayload);
        await client.WriteEnvelopeAsync(helloEnvelope, cancellationToken).ConfigureAwait(false);

        var response = await client.ReadEnvelopeAsync(cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            throw new ProfilingProtocolException("Connection closed by server during handshake.");
        }

        if (response.MessageType != ProfilingMessageType.HelloAck)
        {
            throw new ProfilingProtocolException(
                $"Expected HelloAck response from server, but received {response.MessageType}.");
        }

        if (response.ProtocolVersion != ProfilingProtocolVersion.Current)
        {
            throw new ProfilingProtocolException(
                $"HelloAck envelope protocol version {response.ProtocolVersion} is incompatible with worker protocol version {ProfilingProtocolVersion.Current}.");
        }

        if (!string.Equals(response.RequestId, helloEnvelope.RequestId, StringComparison.Ordinal))
        {
            throw new ProfilingProtocolException("HelloAck response did not echo the Hello requestId.");
        }

        var ack = response.DeserializePayload<HelloAckPayload>();
        if (ack is null)
        {
            throw new ProfilingProtocolException("Failed to parse HelloAck payload from server.");
        }

        if (ack.ProtocolVersion != ProfilingProtocolVersion.Current)
        {
            throw new ProfilingProtocolException(
                $"HelloAck payload protocol version {ack.ProtocolVersion} is incompatible with worker protocol version {ProfilingProtocolVersion.Current}.");
        }

        if (!ack.AcceptedCompatibility)
        {
            throw new ProfilingProtocolException(
                $"Handshake rejected by server: {ack.ErrorMessage ?? "Incompatible protocol or version."}");
        }

        return ack;
    }

    private static bool IsModelAvailable(
        string modelId,
        string artifactFileName,
        string expectedSha256,
        string logicalName)
    {
        string path;
        try
        {
            path = WorkerRuntimeEnvironment.Current.ResolveModelPath(modelId, artifactFileName);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or InvalidOperationException)
        {
            return false;
        }

        try
        {
            FaceModelValidator.ValidateOrThrow(path, expectedSha256, logicalName);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or FaceModelsUnavailableException)
        {
            return false;
        }
    }
}
