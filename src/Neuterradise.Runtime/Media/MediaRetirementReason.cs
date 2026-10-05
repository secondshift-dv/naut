namespace Neuterradise.App.Media;

/// <summary>
/// The one canonical reason a candidate asset retired:
/// <c>SKIPPED | CANCELLED | INVALID | DEDUP_REUSED</c>. It is persisted only together with
/// <see cref="MediaState.Retired"/>.
/// </summary>
public enum MediaRetirementReason
{
    /// <summary>The user chose to skip the item.</summary>
    Skipped,

    /// <summary>The import scope was cancelled before the candidate could commit.</summary>
    Cancelled,

    /// <summary>The candidate could not be admitted as valid media.</summary>
    Invalid,

    /// <summary>An exact duplicate decision reused an existing active asset instead.</summary>
    DedupReused,
}
