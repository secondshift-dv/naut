namespace Neuterradise.App.Media.Model;

public sealed record ModelPreviewDescriptor(
    Guid MediaId,
    string? StaticThumbnailPath = null,
    IReadOnlyList<string>? TurntableFramePaths = null,
    string? Format = null,
    bool HasTurntable = false,
    string? ErrorDetail = null,
    string? AdapterId = null,
    bool IsInteractive = false);
