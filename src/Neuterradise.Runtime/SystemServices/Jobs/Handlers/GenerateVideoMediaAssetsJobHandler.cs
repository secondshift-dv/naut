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
