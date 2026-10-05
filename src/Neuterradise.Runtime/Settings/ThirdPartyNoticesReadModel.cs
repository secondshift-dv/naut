namespace Neuterradise.App.Settings;

public sealed record ThirdPartyComponent(string Name, string License, Uri Source, Uri LicensePage);

public sealed record ThirdPartyNoticesReadModel(
    bool IsAvailable,
    string Content,
    string? SafeError = null)
{
    public static ThirdPartyNoticesReadModel Missing(string reason) =>
        new(false, string.Empty, reason);
}
