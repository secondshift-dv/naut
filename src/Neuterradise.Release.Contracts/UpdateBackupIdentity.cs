using System.Security.Cryptography;
using System.Text;

namespace Neuterradise.Release.Contracts;

/// <summary>Operation-bound inventory of the exact pre-replacement install tree.
/// Recovery never infers backup identity from the sibling directory name alone.</summary>
public static class UpdateBackupIdentity
{
    public static string Compute(string root, CancellationToken cancellationToken = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Record(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        void Visit(string directory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var pins = UpdatePathGuard.PinDirectory(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdatePathGuard.Resolve(entry);
                var name = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (Directory.Exists(entry))
                {
                    Record("D"); Record(name); Visit(entry);
                }
                else
                {
                    using var stream = UpdatePathGuard.OpenReadFile(entry);
                    Record("F"); Record(name); Record(stream.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    hash.AppendData(SHA256.HashData(stream));
                }
            }
        }
        Visit(root);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static void RecordBeforeMove(string operationDir, string installRoot, string handoffSha256,
        CancellationToken cancellationToken)
    {
        using var pins = UpdatePathGuard.PinDirectory(operationDir);
        var digest = Compute(installRoot, cancellationToken);
        using var stream = new FileStream(Path.Combine(operationDir, "backup.identity"), FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(Encoding.ASCII.GetBytes(handoffSha256 + "\n" + digest));
        stream.Flush(flushToDisk: true);
    }

    public static bool Matches(string operationDir, string root, string handoffSha256,
        CancellationToken cancellationToken)
    {
        var identityPath = Path.Combine(operationDir, "backup.identity");
        using var pins = UpdatePathGuard.PinDirectory(operationDir);
        if (!File.Exists(identityPath)) return false;
        using var stream = UpdatePathGuard.OpenReadFile(identityPath);
        if (stream.Length != 129) return false;
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return string.Equals(reader.ReadToEnd(), handoffSha256 + "\n" + Compute(root, cancellationToken),
            StringComparison.Ordinal);
    }
}
