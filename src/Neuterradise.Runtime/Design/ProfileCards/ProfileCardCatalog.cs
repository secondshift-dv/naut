namespace Neuterradise.App.Design.ProfileCards;

public static class ProfileCardCatalog
{
    public const string FallbackVariantId = "landscape";

    private static readonly HashSet<string> BuiltInVariantIds = new(StringComparer.Ordinal)
    {
        "cinematic",
        "cinematic-tall",
        "poster",
        "portrait-frame",
        "landscape",
        "hero-card",
        "glass",
        "editorial",
        "compact",
        "minimal",
        "stats",
        "prestige",
    };

    public static bool IsKnownVariant(string? id) =>
        id is not null && BuiltInVariantIds.Contains(id);
}
