namespace Neuterradise.App.Trash;

public sealed record TrashItemSummary(
    Guid TrashEntryId,
    string EntityType,
    Guid EntityId,
    string? DisplayName,
    string State,
    string? RecoveryRelativePath,
    string PlanJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long RowVersion);

public sealed record TrashHistoryPage(
    IReadOnlyList<TrashItemSummary> Items,
    string? NextPageToken,
    bool HasMore,
    long TotalCount);

public sealed record ActiveTrashPage(
    IReadOnlyList<TrashItemSummary> Items,
    string? NextPageToken,
    bool HasMore);
