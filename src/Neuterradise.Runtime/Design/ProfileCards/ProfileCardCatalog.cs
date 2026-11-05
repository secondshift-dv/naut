namespace Neuterradise.App.Design.ProfileCards;

public static class ProfileCardCatalog
{
    public const string FallbackVariantId = "editorial-v2";

    private static readonly HashSet<string> BuiltInVariantIds = new(StringComparer.Ordinal)
    {
        "poster-v2", "editorial-v2", "identity",
    };

    public static string Normalize(string? id) => id switch
    {
        "editorial" or "compact" or "minimal" => "editorial-v2",
        "stats" or "glass" or "hero-card" or "prestige" or "identity-plate" => "identity",
        "cinematic" or "cinematic-tall" or "poster" or "portrait-frame"
            or "landscape" or "cinematic-v2" => "poster-v2",
        _ when id is not null && BuiltInVariantIds.Contains(id) => id,
        _ => FallbackVariantId,
    };

    public static bool IsKnownVariant(string? id) =>
        id is not null && (BuiltInVariantIds.Contains(id) || Normalize(id) != FallbackVariantId
            || id is "editorial" or "compact" or "minimal" or "landscape");
}
