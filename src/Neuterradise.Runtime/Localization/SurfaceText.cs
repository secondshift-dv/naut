using System.Globalization;
using Neuterradise.App.Presentation;

namespace Neuterradise.App.Localization;

public static class SurfaceText
{
    public const string LanguageKey = "app.uiLanguage";
    public static event EventHandler? LanguageChanged;
    public static string CurrentLanguage { get; private set; } = LanguageCatalog.DefaultCode;

    public static IReadOnlyList<LanguageDefinition> SupportedLanguages => LanguageCatalog.Supported;

    public static string Get(string key, string fallback) =>
        LocalizationTables.TryGet(CurrentLanguage, key, out var tabled) ? tabled : fallback;

    public static string Get(string key) => Get(key, key);

    public static string Format(string key, string formatFallback, params object[] args)
    {
        var format = Get(key, formatFallback);
        return string.Format(CultureInfo.CurrentUICulture, format, args);
    }

    public static string PresentationName(CompiledDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Origin == PackOrigin.BuiltIn
            ? Get($"Presentation.Definition.{definition.Ref.DefinitionId}.Name", definition.Name)
            : definition.Name;
    }

    public static string? PresentationDescription(CompiledDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Origin != PackOrigin.BuiltIn)
        {
            return definition.Description;
        }

        var localized = Get(
            $"Presentation.Definition.{definition.Ref.DefinitionId}.Description",
            definition.Description ?? string.Empty);
        return string.IsNullOrWhiteSpace(localized) ? null : localized;
    }

    public static bool ApplyLanguage(string? languageCode)
    {
        var language = LanguageCatalog.Resolve(languageCode);
        var changed = !string.Equals(CurrentLanguage, language.Code, StringComparison.Ordinal);
        CurrentLanguage = language.Code;

        var uiCulture = CultureInfo.GetCultureInfo(language.CultureName);
        CultureInfo.CurrentUICulture = uiCulture;
        CultureInfo.DefaultThreadCurrentUICulture = uiCulture;
        Thread.CurrentThread.CurrentUICulture = uiCulture;

        if (changed)
        {
            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }

        return changed;
    }

    public static IReadOnlyDictionary<string, string> GetStrings(string languageCode) =>
        LocalizationTables.Table(LanguageCatalog.NormalizeOrDefault(languageCode));

    public static void ValidateRequiredKeys(
        string languageCode,
        IEnumerable<string> requiredKeys,
        Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(requiredKeys);
        var normalized = LanguageCatalog.NormalizeOrDefault(languageCode);
        foreach (var key in requiredKeys)
        {
            if (!LocalizationTables.TryGetExact(normalized, key, out _))
            {
                diagnostic?.Invoke($"Missing localization key for {normalized}: {key}");
            }
        }
    }
}
