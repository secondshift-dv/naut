namespace Neuterradise.App.SystemServices.Updates;

public enum UpdateAuthorityReadStatus
{
    Missing,
    Valid,
    Corrupt
}

public sealed record UpdateAuthorityReadResult<T>(
    UpdateAuthorityReadStatus Status,
    T? Value,
    string? SafeError)
    where T : class
{
    public bool IsMissing => Status == UpdateAuthorityReadStatus.Missing;
    public bool IsValid => Status == UpdateAuthorityReadStatus.Valid && Value is not null;
    public bool IsCorrupt => Status == UpdateAuthorityReadStatus.Corrupt;

    public static UpdateAuthorityReadResult<T> Missing() =>
        new(UpdateAuthorityReadStatus.Missing, null, null);

    public static UpdateAuthorityReadResult<T> Valid(T value) =>
        new(UpdateAuthorityReadStatus.Valid, value ?? throw new ArgumentNullException(nameof(value)), null);

    public static UpdateAuthorityReadResult<T> Corrupt(string safeError) =>
        new(
            UpdateAuthorityReadStatus.Corrupt,
            null,
            string.IsNullOrWhiteSpace(safeError)
                ? "Persisted update authority is corrupt or unreadable."
                : safeError);
}
