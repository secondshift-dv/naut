using System.IO;
using Neuterradise.App.Media.Model;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.Maintenance;

internal enum OriginalInspectionStatus { Match, Missing, Mismatch, Unreadable }

internal static class CanonicalOriginalInspection
{
    internal sealed record OriginalFile(string RelativePath, long Length, string Sha256);

    internal static IReadOnlyList<OriginalFile>? Files(HealthMediaItem media,
        IReadOnlyList<MediaComponentRecord> components)
    {
        if (media.CurrentManagedRelativePath is not { Length: > 0 }
            || media.CurrentManagedFileName is not { Length: > 0 } name
            || media.Sha256 is not { Length: 64 } sha)
            return null;
        var isPackage = components.Count > 1 || components.Any(c => c.ComponentRole == ComponentRole.Dependency);
        if (!isPackage) return [new OriginalFile(name, media.ByteLength, sha)];
        if (media.BundleSha256 is not { Length: 64 } bundle
            || components.Count(c => c.ComponentRole == ComponentRole.Primary) != 1)
            return null;
        var primary = components.Single(c => c.ComponentRole == ComponentRole.Primary);
        if (primary.Sha256 != sha || primary.ByteLength != media.ByteLength
            || ModelPackagePlan.HashBundle(components.Select(c => new ModelPackageFile(
                c.ComponentRelativePath, c.ByteLength, c.Sha256, c.ComponentRole)).ToArray()) != bundle)
            return null;
        if (components.Select(c => c.ComponentRelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != components.Count)
            return null;
        return components.Select(c => new OriginalFile(c.ComponentRelativePath, c.ByteLength, c.Sha256)).ToArray();
    }

    internal static async Task<OriginalInspectionStatus> InspectAsync(
        CatalogDb catalog, HealthMediaItem media, IReadOnlyList<MediaComponentRecord> components,
        bool deep, CancellationToken ct)
    {
        var files = Files(media, components);
        if (files is null) return OriginalInspectionStatus.Unreadable;
        var outcome = OriginalInspectionStatus.Match;
        foreach (var file in files)
        {
            var result = await InspectFileAsync(catalog, $"{media.CurrentManagedRelativePath}/{file.RelativePath}",
                file.Length, file.Sha256, deep, ct).ConfigureAwait(false);
            if (result == OriginalInspectionStatus.Mismatch) return result;
            if (result == OriginalInspectionStatus.Missing) outcome = result;
            else if (result == OriginalInspectionStatus.Unreadable && outcome == OriginalInspectionStatus.Match) outcome = result;
        }
        return outcome;
    }

    internal static async Task<OriginalInspectionStatus> InspectFileAsync(
        CatalogDb catalog, string relative, long length, string sha, bool deep, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var path = catalog.Paths.ResolveVaultRelativePath(VaultPathArea.Profiles, relative);
            if (!deep)
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!SourceIdentityHelper.MatchesOpenedPath(file.SafeFileHandle, path))
                    return OriginalInspectionStatus.Unreadable;
                return file.Length == length ? OriginalInspectionStatus.Match : OriginalInspectionStatus.Mismatch;
            }
            var result = await new ManagedFileVerifier().VerifyAsync(path, length, sha, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return result.Status switch
            {
                ManagedFileVerificationStatus.Match => OriginalInspectionStatus.Match,
                ManagedFileVerificationStatus.Missing => OriginalInspectionStatus.Missing,
                ManagedFileVerificationStatus.LengthMismatch or ManagedFileVerificationStatus.HashMismatch => OriginalInspectionStatus.Mismatch,
                _ => OriginalInspectionStatus.Unreadable,
            };
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { return OriginalInspectionStatus.Missing; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return OriginalInspectionStatus.Unreadable; }
    }
}
