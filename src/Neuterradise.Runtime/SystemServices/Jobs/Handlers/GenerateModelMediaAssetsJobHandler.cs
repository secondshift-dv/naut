namespace Neuterradise.App.SystemServices.Jobs.Handlers;

public interface IGenerateModelMediaAssetsJobOperation : IAuthorizedJobOperation;

public sealed class GenerateModelMediaAssetsJobHandler : AuthorizedJobHandler
{
    public GenerateModelMediaAssetsJobHandler(IGenerateModelMediaAssetsJobOperation operation)
        : base("GenerateModelMediaAssets", [JobLane.Media], "Media", operation, runOffCallingThread: true)
    {
    }
}
