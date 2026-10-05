namespace Neuterradise.App.Localization;

public sealed record LanguageDefinition(
    string Code,
    string CultureName,
    string NativeName,
    string FlagAssetName);

public static class LanguageCatalog
{
    public const string DefaultCode = "en";

    public static readonly IReadOnlyList<LanguageDefinition> Supported =
    [
        new("en", "en-US", "English", "us"),
        new("id", "id-ID", "Bahasa Indonesia", "id"),
        new("ja", "ja-JP", "日本語", "jp"),
        new("ko", "ko-KR", "한국어", "kr"),
        new("zh-Hans", "zh-CN", "简体中文", "cn"),
        new("de", "de-DE", "Deutsch", "de"),
        new("fr", "fr-FR", "Français", "fr"),
        new("es", "es-ES", "Español", "es"),
    ];

    private static readonly IReadOnlyDictionary<string, LanguageDefinition> ByCode =
        Supported.ToDictionary(language => language.Code, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> SupportedCodes { get; } =
        Supported.Select(language => language.Code).ToArray();

    public static LanguageDefinition Default => ByCode[DefaultCode];

    public static bool TryGet(string? languageCode, out LanguageDefinition language)
    {
        if (!string.IsNullOrWhiteSpace(languageCode)
            && ByCode.TryGetValue(languageCode.Trim(), out var found))
        {
            language = found;
            return true;
        }

        language = Default;
        return false;
    }

    public static LanguageDefinition Resolve(string? languageCode) =>
        TryGet(languageCode, out var language) ? language : Default;

    public static string NormalizeOrDefault(string? languageCode) =>
        Resolve(languageCode).Code;
}
