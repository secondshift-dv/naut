namespace Neuterradise.App.SystemServices.Database;

public sealed record MediaAssetRecord(
    Guid MediaAssetId, Guid MediaId, MediaAssetRole Role, int ContractVersion,
    MediaAssetState State, string RelativePath, long ByteLength, string Sha256,
    int? PixelWidth, int? PixelHeight, long? DurationMs, long? SourceTimestampMs);

public sealed record ProfileMediaSelection(
    Guid ProfileId, Guid? CoverMediaAssetId, Guid? BannerMediaAssetId);
