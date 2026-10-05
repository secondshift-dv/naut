using System.Buffers;
using System.IO;
using System.Security.Cryptography;

namespace Neuterradise.App.SystemServices.Storage;

public sealed class ManagedFileVerifier
{
    private const int _bufferSize = 1024 * 1024;

    public async Task<ManagedFileVerificationResult> VerifyAsync(
        string path,
        long expectedByteLength,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (expectedByteLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedByteLength));
        }

        ValidateSha256(expectedSha256);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Length and digest come from one handle that denies concurrent writers/deleters.
            // This prevents a metadata/hash TOCTOU window from authorizing cleanup.
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                _bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actualByteLength = stream.Length;
            if (actualByteLength != expectedByteLength)
            {
                return new ManagedFileVerificationResult(
                    ManagedFileVerificationStatus.LengthMismatch,
                    actualByteLength);
            }

            var actualSha256 = await ComputeSha256Async(stream, cancellationToken)
                .ConfigureAwait(false);
            return string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal)
                ? new ManagedFileVerificationResult(
                    ManagedFileVerificationStatus.Match,
                    actualByteLength,
                    actualSha256)
                : new ManagedFileVerificationResult(
                    ManagedFileVerificationStatus.HashMismatch,
                    actualByteLength,
                    actualSha256);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ManagedFileVerificationResult(ManagedFileVerificationStatus.Cancelled);
        }
        catch (FileNotFoundException)
        {
            return new ManagedFileVerificationResult(ManagedFileVerificationStatus.Missing);
        }
        catch (DirectoryNotFoundException)
        {
            return new ManagedFileVerificationResult(ManagedFileVerificationStatus.Missing);
        }
        catch (UnauthorizedAccessException)
        {
            return new ManagedFileVerificationResult(
                ManagedFileVerificationStatus.ReadFailed,
                SafeErrorDetail: "Managed file access was denied.");
        }
        catch (IOException)
        {
            return new ManagedFileVerificationResult(
                ManagedFileVerificationStatus.ReadFailed,
                SafeErrorDetail: "Managed file could not be read.");
        }
    }

    private static async Task<string> ComputeSha256Async(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(
                        buffer.AsMemory(0, _bufferSize),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ValidateSha256(string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        if (expectedSha256.Length != 64
            || expectedSha256.Any(
                character => !char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "Expected SHA-256 must be 64 lowercase hexadecimal characters.",
                nameof(expectedSha256));
        }
    }
}

public enum ManagedFileVerificationStatus
{
    Match,
    Missing,
    LengthMismatch,
    HashMismatch,
    ReadFailed,
    Cancelled,
}

public sealed record ManagedFileVerificationResult(
    ManagedFileVerificationStatus Status,
    long? ActualByteLength = null,
    string? ActualSha256 = null,
    string? SafeErrorDetail = null)
{
    public bool IsMatch => Status == ManagedFileVerificationStatus.Match;
}
