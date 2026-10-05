namespace Neuterradise.App.Media;

/// <summary>
/// Canonical persisted lifecycle of an asset:
/// <c>CANDIDATE | ACTIVE | TRASHED | RETIRED</c>. Only <see cref="Retired"/> carries an
/// <see cref="MediaRetirementReason"/>; every other state stores NULL for that column.
/// Database text is produced and consumed only through <c>DbEnum</c>.
/// </summary>
public enum MediaState
{
    /// <summary>A pre-commit intake record. Not user-visible library media.</summary>
    Candidate,

    /// <summary>Committed library media.</summary>
    Active,

    /// <summary>Restorable until purge.</summary>
    Trashed,

    /// <summary>A candidate that will never become library media, with one canonical reason.</summary>
    Retired,
}
