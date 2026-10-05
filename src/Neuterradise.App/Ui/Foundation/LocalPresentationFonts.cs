using System.IO;
using System.Security.Cryptography;
using Microsoft.UI.Xaml.Documents.TextFormatting;
using Neuterradise.App.Presentation;
using SkiaSharp;
using Windows.UI.Text;

namespace Neuterradise.App.Ui;

public sealed class LocalPresentationFonts : IFontFallbackService
{
    private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal);
    public static LocalPresentationFonts Current { get; } = new();

    public string Register(TypographyFace face)
    {
        if (face.AssetPath is not { } path) return face.Family;
        using var stream = File.OpenRead(path);
        var alias = "naut-font-" + Convert.ToHexString(SHA256.HashData(stream));
        lock (_paths) _paths[alias] = path;
        return alias;
    }

    public void BeginSelection() { lock (_paths) _paths.Clear(); }

    public Task<string?> GetFontFamilyForCodepoint(int codepoint)
    {
        var language = Neuterradise.App.Localization.SurfaceText.CurrentLanguage;
        string? file = codepoint is >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF ? "notosanskr.ttf"
            : codepoint is >= 0x3040 and <= 0x30FF ? "notosansjp.ttf"
            : codepoint is >= 0x3400 and <= 0x9FFF or >= 0x20000 and <= 0x3134F
                ? language == "ja" ? "notosansjp.ttf" : language == "ko" ? "notosanskr.ttf" : "notosanssc.ttf" : null;
        if (file is not null) return Task.FromResult<string?>("naut-cjk-" + file);
        using var typeface = SKFontManager.Default.MatchCharacter(codepoint);
        return Task.FromResult(typeface?.FamilyName);
    }

    public Task<Stream?> GetFontStreamForFontFamily(string fontFamily, FontWeight weight, FontStretch stretch, FontStyle style)
    {
        string? path;
        lock (_paths) _paths.TryGetValue(fontFamily, out path);
        if (fontFamily.StartsWith("naut-cjk-", StringComparison.Ordinal))
        {
            var file = fontFamily[9..];
            if (file is "notosansjp.ttf" or "notosanskr.ttf" or "notosanssc.ttf")
                path = Path.Combine(AppContext.BaseDirectory, "Assets", "Presentation", "BuiltIn", "Fonts", file);
        }
        return path is null ? Task.FromResult<Stream?>(null) : Task.Run<Stream?>(() =>
        {
            try { return File.OpenRead(path); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        });
    }
}
