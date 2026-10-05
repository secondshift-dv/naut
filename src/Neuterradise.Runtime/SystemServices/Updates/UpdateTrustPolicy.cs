namespace Neuterradise.App.SystemServices.Updates;

public sealed record UpdateTrustConfiguration(
    string? ConfiguredFeed,
    bool AllowExplicitLocalPackage)
{
    public UpdateTrustConfiguration(
        string? configuredFeed,
        string? legacyPublisherKeyId,
        bool allowExplicitLocalPackage)
        : this(configuredFeed, allowExplicitLocalPackage)
    {
        if (!string.IsNullOrWhiteSpace(legacyPublisherKeyId))
        {
            throw new ArgumentException(
                "Publisher key selection is fixed by the embedded release trust contract and cannot be overridden by configuration.",
                nameof(legacyPublisherKeyId));
        }
    }

    public bool HasConfiguredFeed => TryGetConfiguredFeed(out _);

    public bool TryGetConfiguredFeed(out Uri feed)
    {
        feed = null!;
        if (string.IsNullOrWhiteSpace(ConfiguredFeed)
            || !Uri.TryCreate(ConfiguredFeed, UriKind.Absolute, out var candidate)
            || !string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || !string.IsNullOrEmpty(candidate.Fragment))
        {
            return false;
        }

        feed = candidate;
        return true;
    }

    public bool MatchesConfiguredFeed(Uri actualFeed)
    {
        ArgumentNullException.ThrowIfNull(actualFeed);
        return TryGetConfiguredFeed(out var configuredFeed)
            && Uri.Compare(
                configuredFeed,
                actualFeed,
                UriComponents.HttpRequestUrl,
                UriFormat.SafeUnescaped,
                StringComparison.Ordinal) == 0;
    }
}

public sealed record UpdateTrustDecision(
    bool Accepted,
    bool IntegrityVerified,
    bool PublisherVerified,
    bool IsExplicitLocalSource,
    string Reason)
{
    public static UpdateTrustDecision Reject(string reason) =>
        new(false, false, false, false, reason);
}

public sealed class UpdateTrustPolicy
{
    public UpdateTrustDecision Evaluate(
        UpdateManifest manifest,
        UpdateTrustConfiguration configuration,
        bool isLocalPackage,
        bool userConfirmedLocalPackage,
        bool publisherVerified = false)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(configuration);
        try
        {
            manifest.Validate();
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return UpdateTrustDecision.Reject(exception.Message);
        }

        if (!string.Equals(manifest.RuntimeIdentifier, ProductIdentity.RuntimeId, StringComparison.OrdinalIgnoreCase))
            return UpdateTrustDecision.Reject("Update runtime is incompatible.");

        Version candidateVersion;
        Version installedVersion;
        try
        {
            candidateVersion = UpdateManifest.ParseStrictVersion(manifest.ProductVersion, "Update product version");
            installedVersion = UpdateManifest.ParseStrictVersion(ProductIdentity.Version, "Installed product version");
        }
        catch (FormatException exception)
        {
            return UpdateTrustDecision.Reject(exception.Message);
        }

        if (candidateVersion <= installedVersion)
            return UpdateTrustDecision.Reject("Update version is not newer than the installed product.");

        if (manifest.MinimumCompatibleVersion is { } minimumCompatibleVersion)
        {
            Version minimum;
            try
            {
                minimum = UpdateManifest.ParseStrictVersion(minimumCompatibleVersion, "Minimum compatible version");
            }
            catch (FormatException exception)
            {
                return UpdateTrustDecision.Reject(exception.Message);
            }

            if (installedVersion < minimum)
                return UpdateTrustDecision.Reject("Installed product is older than the update's minimum compatible version.");
        }
        else if (candidateVersion.Major > installedVersion.Major)
        {
            return UpdateTrustDecision.Reject("A cross-major update must declare minimumCompatibleVersion explicitly.");
        }

        if (isLocalPackage)
        {
            if (!configuration.AllowExplicitLocalPackage || !userConfirmedLocalPackage)
                return UpdateTrustDecision.Reject("Local update requires explicit confirmation.");

            return new UpdateTrustDecision(
                Accepted: true,
                IntegrityVerified: false,
                PublisherVerified: false,
                IsExplicitLocalSource: true,
                Reason: "Local package is an explicit user trust override; package integrity must pass before staging or apply.");
        }

        if (!configuration.HasConfiguredFeed)
            return UpdateTrustDecision.Reject("No valid configured HTTPS update feed is available.");
        if (!publisherVerified)
            return UpdateTrustDecision.Reject("Update metadata publisher authenticity could not be verified.");

        return new UpdateTrustDecision(
            Accepted: true,
            IntegrityVerified: false,
            PublisherVerified: true,
            IsExplicitLocalSource: false,
            Reason: "Update metadata is publisher-authenticated from the configured HTTPS feed; payload integrity must pass before staging or apply.");
    }
}
