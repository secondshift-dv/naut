namespace Neuterradise.App.Profiles;

/// <summary>
/// Canonical persisted profile kind: <c>NORMAL | UNKNOWN</c>. Resolving an Unknown profile to
/// Normal keeps the same ProfileId; the kind column is the only authority for the distinction and
/// is mapped only through <c>DbEnum</c>.
/// </summary>
public enum ProfileKind
{
    /// <summary>Has a persisted display name and full presentation authority.</summary>
    Normal,

    /// <summary>Has no persisted display name; the surface derives Unknown N from its sequence.</summary>
    Unknown,
}
