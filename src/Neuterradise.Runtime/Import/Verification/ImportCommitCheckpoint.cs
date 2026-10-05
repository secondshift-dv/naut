namespace Neuterradise.App.Import.Verification;

/// <summary>
/// The durable commit checkpoint of one import unit, persisted in
/// <c>import_units.library_commit_state</c> and in the commit operation row.
/// <para>
/// The members are ordered so that <c>&lt;</c> comparisons mean "has not reached this step yet".
/// <see cref="NotCommitted"/> is the default: a unit that has not started committing must never
/// look like it already validated its decisions.
/// </para>
/// </summary>
public enum ImportCommitCheckpoint
{
    /// <summary>No commit work is durable yet; cancellation is still fully safe.</summary>
    NotCommitted,

    /// <summary>The plan, destination and item decisions were validated.</summary>
    DecisionValidated,

    /// <summary>Destination profile/identity and paths are allocated in the durable plan.</summary>
    DestinationPrepared,

    /// <summary>Final managed placement is planned for every included item.</summary>
    PlacementPlanned,

    /// <summary>Published bytes were re-read and verified against hash and length.</summary>
    DestinationBytesVerified,

    /// <summary>The single catalog transaction committed; the library now owns this media.</summary>
    DomainAuthorityCommitted,

    /// <summary>Authorized source cleanup is pending for MOVE items.</summary>
    SourceCleanupPending,

    /// <summary>Source cleanup finished with a final disposition for every item.</summary>
    SourceCleanupComplete,

    /// <summary>The commit operation is closed; nothing further will run for it.</summary>
    Terminal,
}
