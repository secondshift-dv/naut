namespace Neuterradise.App.SystemServices.Jobs.Handlers;

public interface IGenerateVideoMediaAssetsJobOperation : IAuthorizedJobOperation;

public sealed class GenerateVideoMediaAssetsJobHandler : AuthorizedJobHandler
{
    public GenerateVideoMediaAssetsJobHandler(IGenerateVideoMediaAssetsJobOperation operation)
        : base("GenerateVideoMediaAssets", [JobLane.Media], "Media", operation,
            runOffCallingThread: true)
    {
    }
}

public sealed class GenerateBannerJobHandler : AuthorizedJobHandler
{
    public GenerateBannerJobHandler(IGenerateVideoMediaAssetsJobOperation operation)
        : base("GenerateBanner", [JobLane.Media], "Media", operation, runOffCallingThread: true) { }
}
