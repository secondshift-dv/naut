using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Xml.Linq;

namespace Neuterradise.App.Localization;

internal static class LocalizationTables
{
    private static readonly ConcurrentDictionary<string, Lazy<FrozenDictionary<string, string>>> Tables =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string languageCode, string key, out string value)
    {
        var language = LanguageCatalog.NormalizeOrDefault(languageCode);
        if (TryGetExact(language, key, out value))
        {
            return true;
        }

        if (!string.Equals(language, LanguageCatalog.DefaultCode, StringComparison.OrdinalIgnoreCase)
            && TryGetExact(LanguageCatalog.DefaultCode, key, out value))
        {
            return true;
        }

        value = string.Empty;
        return false;
    }

    public static bool TryGetExact(string languageCode, string key, out string value)
    {
        var table = Table(languageCode);
        if (table.TryGetValue(key, out var found) && !string.IsNullOrEmpty(found))
        {
            value = found;
            return true;
        }

        value = string.Empty;
        return false;
    }

    public static IReadOnlyDictionary<string, string> Table(string languageCode)
    {
        var language = LanguageCatalog.NormalizeOrDefault(languageCode);
        return Tables.GetOrAdd(
            language,
            static code => new Lazy<FrozenDictionary<string, string>>(
                () => Load(code),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static FrozenDictionary<string, string> Load(string language)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = typeof(LocalizationTables).Assembly
                .GetManifestResourceStream($"Neuterradise.App.Localization.Strings.{language}.xml");
            if (stream is null)
            {
                return result.ToFrozenDictionary(StringComparer.Ordinal);
            }

            var document = XDocument.Load(stream);
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            foreach (var element in document.Root?.Elements() ?? [])
            {
                var key = (string?)element.Attribute(x + "Key");
                if (!string.IsNullOrEmpty(key))
                {
                    result[key] = element.Value;
                }
            }
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or IOException)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Localization table {0} could not be read: {1}",
                language,
                exception.GetType().Name);
        }

        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
