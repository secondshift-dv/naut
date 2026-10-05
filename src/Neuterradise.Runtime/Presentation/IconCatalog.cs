using System.Text.RegularExpressions;

namespace Neuterradise.App.Presentation;

/// <summary>
/// Semantic icon keys (document 01 §18) and the built-in line geometry every icon pack falls back to.
/// Icons never change action semantics; navigation keeps its text labels.
/// </summary>
public static partial class IconCatalog
{
    /// <summary>The single application icon system: Neu Line.</summary>
    public const float StrokeWidth = 1.6f;

    private static readonly Dictionary<string, string> BuiltIn = new(StringComparer.Ordinal)
    {
        ["icon.brand"] = "M12 1.6 A10.4 10.4 0 1 0 12 22.4 A10.4 10.4 0 1 0 12 1.6 Z M12 5.2 A6.8 6.8 0 1 1 12 18.8 A6.8 6.8 0 1 1 12 5.2 Z M12 8.4 L15.6 12 L12 15.6 L8.4 12 Z",
        ["icon.navigation.home"] = "M4.6 11.2 L12 4.8 L19.4 11.2 V19.2 H4.6 Z",
        ["icon.navigation.home.detail"] = "M12 9.2 A3.1 3.1 0 1 0 12 15.4 A3.1 3.1 0 1 0 12 9.2 Z M12 10.6 V14",
        ["icon.navigation.gallery"] = "M5.4 6.2 H20 V19.2 H5.4 Z",
        ["icon.navigation.gallery.detail"] = "M3.5 4.2 H18.1 V6.2 M7.5 15.4 L10.6 12.2 L13.5 14.8 L16.4 11.8 L20 15.2",
        ["icon.navigation.import"] = "M4.5 15.5 V18.5 A1.5 1.5 0 0 0 6 20 H18 A1.5 1.5 0 0 0 19.5 18.5 V15.5",
        ["icon.navigation.import.detail"] = "M12 3.6 V14 M8.5 10.5 L12 14 L15.5 10.5",
        ["icon.navigation.settings"] = "M12 3.2 A8.8 8.8 0 1 0 12 20.8 A8.8 8.8 0 1 0 12 3.2 Z M12 3.2 V5.1 M12 18.9 V20.8 M3.2 12 H5.1 M18.9 12 H20.8",
        ["icon.navigation.settings.detail"] = "M12 8.6 A3.4 3.4 0 1 0 12 15.4 A3.4 3.4 0 1 0 12 8.6 Z M6.4 6.4 L7.8 7.8 M16.2 16.2 L17.6 17.6 M17.6 6.4 L16.2 7.8 M7.8 16.2 L6.4 17.6",
        ["icon.navigation.customize"] = "M12 4 A8 8 0 1 0 12 20 A8 8 0 1 0 12 4 Z M18.2 5.8 L20 4 M5.8 18.2 L4 20",
        ["icon.navigation.customize.detail"] = "M12 6.5 L13.2 10.8 L17.5 12 L13.2 13.2 L12 17.5 L10.8 13.2 L6.5 12 L10.8 10.8 Z",
        ["icon.navigation.history"] = "M6.4 6.4 A8 8 0 1 1 4.5 14.5 M6.4 6.4 V10.5 M6.4 6.4 H10.5 M12 7.5 V12 L15.2 13.8",
        ["icon.action.back"] = "M20 12 H4.5 M11 5.5 L4.5 12 L11 18.5",
        ["icon.action.edit"] = "M4.5 19.5 V15.5 L15.5 4.5 L19.5 8.5 L8.5 19.5 Z M13.5 6.5 L17.5 10.5",
        ["icon.action.note"] = "M5 3.5 H19 A1.5 1.5 0 0 1 20.5 5 V19 A1.5 1.5 0 0 1 19 20.5 H5 A1.5 1.5 0 0 1 3.5 19 V5 A1.5 1.5 0 0 1 5 3.5 Z M7.5 8 H16.5 M7.5 12 H16.5 M7.5 16 H13",
        ["icon.action.delete"] = "M4.5 6.5 H19.5 M9.5 6.5 V4.5 H14.5 V6.5 M6.5 6.5 V19 A1.5 1.5 0 0 0 8 20.5 H16 A1.5 1.5 0 0 0 17.5 19 V6.5 M10 10 V17 M14 10 V17",
        ["icon.action.restore"] = "M6.34 6.34 A8 8 0 1 1 4.6 15.2 M6.34 6.34 V10.6 M6.34 6.34 H10.6",
        ["icon.action.favorite"] = "M12 20 C12 20 3.5 14.8 3.5 9.6 C3.5 6.9 5.7 4.8 8.3 4.8 C10 4.8 11.3 5.7 12 6.9 C12.7 5.7 14 4.8 15.7 4.8 C18.3 4.8 20.5 6.9 20.5 9.6 C20.5 14.8 12 20 12 20 Z",
        ["icon.action.search"] = "M10.5 4 A6.5 6.5 0 1 0 10.5 17 A6.5 6.5 0 1 0 10.5 4 M15.5 15.5 L20.5 20.5",
        ["icon.action.more"] = "M5 12 A1.25 1.25 0 1 0 7.5 12 A1.25 1.25 0 1 0 5 12 M10.75 12 A1.25 1.25 0 1 0 13.25 12 A1.25 1.25 0 1 0 10.75 12 M16.5 12 A1.25 1.25 0 1 0 19 12 A1.25 1.25 0 1 0 16.5 12",
        ["icon.action.add"] = "M12 4.5 V19.5 M4.5 12 H19.5",
        ["icon.action.open"] = "M14 4.5 H19.5 V10 M19.5 4.5 L11 13 M17 14.5 V18.5 A1.5 1.5 0 0 1 15.5 20 H6 A1.5 1.5 0 0 1 4.5 18.5 V9 A1.5 1.5 0 0 1 6 7.5 H10",
        ["icon.action.open-folder"] = "M3.5 6.5 A1.5 1.5 0 0 1 5 5 H9.5 L11.5 7.5 H19 A1.5 1.5 0 0 1 20.5 9 V17.5 A1.5 1.5 0 0 1 19 19 H5 A1.5 1.5 0 0 1 3.5 17.5 Z M9 13.5 H15.5 M13 11 L15.5 13.5 L13 16",
        ["icon.action.play"] = "M8 5.5 L18.5 12 L8 18.5 Z",
        ["icon.action.pause"] = "M9 5.5 V18.5 M15 5.5 V18.5",
        ["icon.action.chevron"] = "M6.5 9.5 L12 15 L17.5 9.5",
        ["icon.action.close"] = "M6 6 L18 18 M18 6 L6 18",
        ["icon.action.crop"] = "M6.5 2.8 V17.5 H21.2 M2.8 6.5 H17.5 V21.2",
        ["icon.action.layout"] = "M3.5 4.5 H20.5 V19.5 H3.5 Z M3.5 9.5 H20.5 M9.5 9.5 V19.5",
        ["icon.action.card"] = "M4.5 3.5 H19.5 V20.5 H4.5 Z M4.5 14.5 H19.5 M7.5 17.2 H14",
        ["icon.media.image"] = "M3.5 5 H20.5 V19 H3.5 Z M3.5 15.5 L9 10.5 L12.5 14 L15.5 11.5 L20.5 16 M14.6 8.6 A1.3 1.3 0 1 1 17.2 8.6 A1.3 1.3 0 1 1 14.6 8.6",
        ["icon.media.video"] = "M3.5 6 H14.5 V18 H3.5 Z M14.5 10.5 L20.5 7 V17 L14.5 13.5 Z",
        ["icon.media.model"] = "M12 3.2 L20 7.6 V16.4 L12 20.8 L4 16.4 V7.6 Z M4 7.6 L12 12 L20 7.6 M12 12 V20.8",
        ["icon.media.open"] = "M4 6 H11 L13 8 H20 V18.5 A1.5 1.5 0 0 1 18.5 20 H5.5 A1.5 1.5 0 0 1 4 18.5 Z M13.5 11.5 H19.5 V17.5 M19.5 11.5 L11.5 19.5",
        ["icon.media.add"] = "M3.5 5 H15.5 V19 H3.5 Z M3.5 15.5 L8.5 10.5 L12 14 M12.8 8.2 A1.2 1.2 0 1 1 15.2 8.2 A1.2 1.2 0 1 1 12.8 8.2 M18 10.5 V19.5 M13.5 15 H22.5",
        ["icon.profile.person"] = "M12 4.2 A3.6 3.6 0 1 1 12 11.4 A3.6 3.6 0 1 1 12 4.2 M4.8 20.2 C4.8 16.4 8 14 12 14 C16 14 19.2 16.4 19.2 20.2",
        ["icon.profile.favorite"] = "M12 20.2 C12 20.2 3.6 15.2 3.6 9.4 C3.6 6.7 5.7 4.8 8.1 4.8 C9.8 4.8 11.2 5.8 12 7.1 C12.8 5.8 14.2 4.8 15.9 4.8 C18.3 4.8 20.4 6.7 20.4 9.4 C20.4 15.2 12 20.2 12 20.2 Z",
        ["icon.profile.related"] = "M8.5 7.5 A4.5 4.5 0 1 0 8.5 16.5 A4.5 4.5 0 1 0 8.5 7.5 M15.5 7.5 A4.5 4.5 0 1 1 15.5 16.5 A4.5 4.5 0 1 1 15.5 7.5",
        ["icon.profile.rating"] = "M12 3.5 L14.6 9.2 L20.8 9.9 L16.2 14.1 L17.5 20.2 L12 17.1 L6.5 20.2 L7.8 14.1 L3.2 9.9 L9.4 9.2 Z",
        ["icon.profile.category"] = "M12 3.5 L21 8 L12 12.5 L3 8 Z M3 12 L12 16.5 L21 12 M3 16 L12 20.5 L21 16",
        ["icon.profile.tag"] = "M3.5 11.5 V4.5 A1 1 0 0 1 4.5 3.5 H11.5 L20.5 12.5 A1.5 1.5 0 0 1 20.5 14.6 L14.6 20.5 A1.5 1.5 0 0 1 12.5 20.5 Z M6.85 7.75 A0.9 0.9 0 1 1 8.65 7.75 A0.9 0.9 0 1 1 6.85 7.75",
        ["icon.profile.face"] = "M12 3.5 A8.5 8.5 0 1 0 12 20.5 A8.5 8.5 0 1 0 12 3.5 M8.75 10 V11.5 M15.25 10 V11.5 M8.5 14.75 A4.5 4.5 0 0 0 15.5 14.75",
        ["icon.profile.assign"] = "M8.5 4.2 A3.2 3.2 0 1 1 8.5 10.6 A3.2 3.2 0 1 1 8.5 4.2 M3.5 19.5 C3.5 15.8 5.8 13.5 9 13.5 C10.7 13.5 12 14 13 14.8 M17.5 11 V19 M13.5 15 H21.5",
        ["icon.status.activity"] = "M3 12 H7 L9.5 5.5 L14 18.5 L16.5 12 H21",
        ["icon.status.storage"] = "M4 6.5 A8 3 0 1 0 20 6.5 A8 3 0 1 0 4 6.5 M4 6.5 V17.5 A8 3 0 0 0 20 17.5 V6.5 M4 12 A8 3 0 0 0 20 12",
        ["icon.vault.identity"] = "M4.4 4.2 H19.6 V19.8 H4.4 Z M12 6.7 A5.3 5.3 0 1 0 12 17.3 A5.3 5.3 0 1 0 12 6.7 Z",
        ["icon.vault.identity.detail"] = "M12 9.1 V12 L14.4 13.4 M6.8 8.5 V15.5 M17.2 8.5 V15.5",
        ["icon.status.health"] = "M12 3 L20 6 V11.5 C20 16.5 16.5 20 12 21.5 C7.5 20 4 16.5 4 11.5 V6 Z M8.75 12 L11 14.25 L15.25 9.5",
        ["icon.status.warning"] = "M12 4 L21.5 20 H2.5 Z M12 10 V14.5 M12 17.1 V17.4",
        ["icon.status.success"] = "M12 3.5 A8.5 8.5 0 1 0 12 20.5 A8.5 8.5 0 1 0 12 3.5 M8 12.2 L11 15.2 L16.2 9.4",
        ["icon.status.error"] = "M12 3.5 A8.5 8.5 0 1 0 12 20.5 A8.5 8.5 0 1 0 12 3.5 M9.2 9.2 L14.8 14.8 M14.8 9.2 L9.2 14.8",
        ["icon.status.info"] = "M12 3.5 A8.5 8.5 0 1 0 12 20.5 A8.5 8.5 0 1 0 12 3.5 M12 11 V16.5 M12 7.6 V8",
        ["icon.window.minimize"] = "M5 12 H19",
        ["icon.window.maximize"] = "M5.5 5.5 H18.5 V18.5 H5.5 Z",
        ["icon.window.restore"] = "M8.5 5.5 H18.5 V15.5 M5.5 8.5 H15.5 V18.5 H5.5 Z",
        ["icon.window.close"] = "M6 6 L18 18 M18 6 L6 18",
    };

    public static IReadOnlyCollection<string> Keys => BuiltIn.Keys;

    public static bool IsKnownKey(string key) => BuiltIn.ContainsKey(key);

    public static string PathFor(string key) => BuiltIn.TryGetValue(key, out var path) ? path : BuiltIn["icon.action.more"];

    [GeneratedRegex(@"^[MmLlHhVvCcSsQqTtAaZz0-9eE\s,.\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PathDataPattern();

    public static bool IsSafePathData(string data) => PathDataPattern().IsMatch(data);
}
