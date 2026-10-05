using System.Globalization;

namespace Neuterradise.App.Profiles;

public static class ProfileRules
{
    public const int MaxDisplayNameLength = 200;

    public const int MaximumDisplayNameTextElements = 200;

    public const int MaximumOverviewLength = 20_000;

    public const int MaximumPrivateNotesLength = 2_000;

    public const int MinimumRating = 0;

    public const int MaximumRating = 5;

    public static string NormalizeDisplayName(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        if (!TryValidateDisplayName(displayName, out var normalized, out var failure))
        {
            throw new ArgumentException(failure, nameof(displayName));
        }

        return normalized;
    }

    public static bool TryValidateDisplayName(
        string? displayName,
        out string normalized,
        out string? failure)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(displayName))
        {
            failure = "A Profile name cannot be empty.";
            return false;
        }

        var trimmed = displayName.Trim();

        foreach (var character in trimmed)
        {
            if (char.IsControl(character))
            {
                failure = "A Profile name cannot contain control characters.";
                return false;
            }
        }

        var textElements = StringInfo.ParseCombiningCharacters(trimmed).Length;
        if (textElements > MaximumDisplayNameTextElements)
        {
            failure = $"A Profile name is limited to {MaximumDisplayNameTextElements} characters.";
            return false;
        }

        normalized = trimmed;
        failure = null;
        return true;
    }

    public static bool IsRatingValid(int? rating) =>
        rating is null || (rating.Value >= MinimumRating && rating.Value <= MaximumRating);

    public static bool IsPrivateNotesValid(string? notes) =>
        notes is null || notes.Length <= MaximumPrivateNotesLength;

    public static bool IsOverviewValid(string? overview) =>
        overview is null || overview.Length <= MaximumOverviewLength;

    public static void ValidateNormalProfile(
        Guid profileId,
        string displayName,
        long? unknownSequence,
        int activeIdentityCount)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Profile identifier cannot be empty.", nameof(profileId));
        }

        NormalizeDisplayName(displayName);

        if (unknownSequence is not null)
        {
            throw new InvalidOperationException("A NORMAL Profile must not have an UnknownSequence.");
        }

        if (activeIdentityCount != 1)
        {
            throw new InvalidOperationException(
                $"A NORMAL Profile must have exactly one active Identity, but had {activeIdentityCount}.");
        }
    }

    public static void ValidateUnknownProfile(
        Guid profileId,
        string? displayName,
        long? unknownSequence,
        int activeIdentityCount)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Profile identifier cannot be empty.", nameof(profileId));
        }

        if (displayName is not null)
        {
            throw new InvalidOperationException(
                "An UNKNOWN Profile has no editable DisplayName authority; DisplayName must be null.");
        }

        if (unknownSequence is null || unknownSequence.Value <= 0)
        {
            throw new InvalidOperationException(
                $"An UNKNOWN Profile must have a positive UnknownSequence, but had {unknownSequence}.");
        }

        if (activeIdentityCount != 0)
        {
            throw new InvalidOperationException(
                $"An UNKNOWN Profile must have zero active Identities, but had {activeIdentityCount}.");
        }
    }

    public static void ValidateIdentity(Guid identityId, Guid profileId, ProfileKind profileKind)
    {
        if (identityId == Guid.Empty)
        {
            throw new ArgumentException("Identity identifier cannot be empty.", nameof(identityId));
        }

        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Profile identifier cannot be empty.", nameof(profileId));
        }

        if (profileKind != ProfileKind.Normal)
        {
            throw new InvalidOperationException(
                $"Only a NORMAL Profile may have an Identity. Kind '{profileKind}' is invalid.");
        }
    }

    public static void ValidateKindTransition(ProfileKind currentKind, ProfileKind newKind)
    {
        if (currentKind == newKind)
        {
            return;
        }

        if (currentKind == ProfileKind.Unknown && newKind == ProfileKind.Normal)
        {
            throw new InvalidOperationException(
                "An UNKNOWN Profile is never converted in place to a NORMAL Profile. Resolution creates or reuses a distinct NORMAL Profile.");
        }

        if (currentKind == ProfileKind.Normal && newKind == ProfileKind.Unknown)
        {
            throw new InvalidOperationException(
                "A NORMAL Profile cannot be converted to an UNKNOWN Profile.");
        }
    }

    public static bool IsZeroSampleIdentityValid => true;
}
