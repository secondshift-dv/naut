namespace Neuterradise.App.SystemServices.Database;

public enum MediaAssetRole
{
    Thumbnail,
    Hover,
    ModelRender,
    Banner,
}

public enum MediaAssetState
{
    Ready,
    NeedsRepair,
}

public static class MediaAssetContracts
{
    public static bool IsSupported(MediaAssetRole role, int version) => role switch
    {
        MediaAssetRole.Thumbnail or MediaAssetRole.ModelRender => version == 1,
        MediaAssetRole.Hover => version is 1 or 2,
        MediaAssetRole.Banner => version == 2,
        _ => false,
    };
}
