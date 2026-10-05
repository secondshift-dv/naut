using Neuterradise.App.Media;

namespace Neuterradise.App.SystemServices.Storage;

public sealed record ManagedPathPlan(
    Guid ProfileId,
    ProfileStorageToken ProfileStorageToken,
    string ProfileFolderRelativePath,
    Guid? MediaId,
    MediaStorageToken? MediaStorageToken,
    MediaType? MediaType,
    string? ManagedFileRelativePath,
    string? ManagedFileName)
{

    public string TargetProfileFolderRelativePath => ProfileFolderRelativePath;

    public string? TargetManagedFileRelativePath => ManagedFileRelativePath;

    public bool IsMediaPlan => MediaId is not null;
}
