namespace Neuterradise.App.SystemServices.Storage;

public sealed record VolumeIdentity
{
    public VolumeIdentity(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim().ToUpperInvariant();
    }

    public string Value { get; }

    public override string ToString() => Value;

    public static bool CanUseSameVolumeMove(
        IVolumeIdentityProvider provider,
        string sourcePath,
        string targetExistingPathOrRoot)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetExistingPathOrRoot);

        var source = provider.TryGetVolumeIdentity(sourcePath);
        var target = provider.TryGetVolumeIdentity(targetExistingPathOrRoot);
        return source is not null && target is not null && source == target;
    }
}
