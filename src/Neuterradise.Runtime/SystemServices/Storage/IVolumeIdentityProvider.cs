namespace Neuterradise.App.SystemServices.Storage;

public interface IVolumeIdentityProvider
{
    VolumeIdentity? TryGetVolumeIdentity(string resolvedPath);
}
