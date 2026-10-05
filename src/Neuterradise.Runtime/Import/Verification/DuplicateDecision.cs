namespace Neuterradise.App.Import.Verification;

/// <summary>
/// The canonical exact-duplicate decision persisted in <c>import_items.duplicate_decision</c>
///: <c>INCLUDE | REUSE | SKIP</c>. <c>NULL</c> means the decision is still
/// open; it is not a fourth value here.
/// </summary>
public enum DuplicateDecision
{
    /// <summary>The candidate becomes its own committed asset.</summary>
    Include,

    /// <summary>No second managed byte copy; the relation points at the existing active asset.</summary>
    Reuse,

    /// <summary>The candidate retires as SKIPPED and creates no library relation.</summary>
    Skip,
}
