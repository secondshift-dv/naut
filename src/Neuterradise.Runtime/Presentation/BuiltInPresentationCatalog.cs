using System.Text.Json;

namespace Neuterradise.App.Presentation;

public static class BuiltInPresentationCatalog
{
    private static readonly Lazy<Catalog> Current = new(Read);

    public static DefinitionRef DefaultFor(string slot) => DefinitionRef.BuiltIn(
        Current.Value.Defaults.TryGetValue(slot, out var id)
            ? id : throw new InvalidOperationException($"No built-in default exists for '{slot}'."));

    public static bool IsPrimary(DefinitionRef reference) => reference.PackId != PresentationContract.BuiltInPackId
        || Current.Value.Primary.Contains(reference.DefinitionId);

    public static DefinitionRef Normalize(DefinitionRef reference) =>
        reference.PackId == PresentationContract.BuiltInPackId && Current.Value.Replacements.TryGetValue(reference.DefinitionId, out var id)
            ? DefinitionRef.BuiltIn(id) : reference;

    internal static string ReadResource(string name)
    {
        using var stream = typeof(BuiltInPresentationCatalog).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Built-in presentation resource '{name}' is unavailable.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Catalog Read()
    {
        using var document = JsonDocument.Parse(ReadResource("Neuterradise.App.Presentation.BuiltIn.catalog.json"));
        var defaults = document.RootElement.GetProperty("defaults").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        var primary = document.RootElement.GetProperty("primary").EnumerateArray()
            .Select(p => p.GetString()!).ToHashSet(StringComparer.Ordinal);
        var replacements = document.RootElement.GetProperty("replacements").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        return new Catalog(defaults, primary, replacements);
    }

    private sealed record Catalog(IReadOnlyDictionary<string, string> Defaults, IReadOnlySet<string> Primary, IReadOnlyDictionary<string, string> Replacements);
}
