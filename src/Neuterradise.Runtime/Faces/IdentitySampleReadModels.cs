using Neuterradise.App.SystemServices.Database.Reads;

namespace Neuterradise.App.Faces;

public sealed record EmbeddingSpaceInventory(
    IReadOnlyList<EmbeddingSpaceKey> Spaces,
    IReadOnlyList<string> UnreadableSpaceKeys);

public sealed record IdentitySampleRecord(
    Guid IdentitySampleId,
    Guid IdentityId,
    Guid ProfileId,
    Guid FaceId,
    Guid MediaId,
    byte[] Embedding,
    EmbeddingSpaceKey EmbeddingSpace,
    string ModelId,
    string ModelVersion,
    DateTimeOffset ConfirmedAtUtc);
