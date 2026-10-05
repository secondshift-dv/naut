using System.Globalization;
using System.IO;
using System.Text;

namespace Neuterradise.App.SystemServices.Storage;

public sealed class ManagedNamePolicy
{
    private const int _maximumProfileNameTextElements = 80;
    private const string _windowsInvalidCharacters = "<>:\"/\\|?*";

    private static readonly HashSet<string> ReservedDeviceNames = CreateReservedDeviceNames();
    private static readonly HashSet<char> InvalidFileNameCharacters =
        [.. Path.GetInvalidFileNameChars(), .. _windowsInvalidCharacters];

    public string ToSafeProfileName(string displayLabel)
    {
        ArgumentNullException.ThrowIfNull(displayLabel);

        var normalized = displayLabel.Normalize(NormalizationForm.FormC);
        var rendered = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (IsInvalid(character))
            {
                if (rendered.Length == 0 || rendered[^1] != '_')
                {
                    rendered.Append('_');
                }

                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (rendered.Length > 0 && rendered[^1] != ' ')
                {
                    rendered.Append(' ');
                }

                continue;
            }

            rendered.Append(character);
        }

        var safeName = TrimPhysicalEnding(rendered.ToString());
        if (safeName.Length == 0)
        {
            safeName = "Profile";
        }

        if (IsReservedDeviceBaseName(safeName))
        {
            safeName += "_";
        }

        safeName = TruncateTextElements(safeName, _maximumProfileNameTextElements);
        safeName = TrimPhysicalEnding(safeName);
        return safeName.Length == 0 ? "Profile" : safeName;
    }

    public string NormalizeExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        var normalized = extension.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.StartsWith(".", StringComparison.Ordinal))
        {
            normalized = normalized[1..];
        }

        if (normalized.Length == 0
            || normalized.Any(character =>
                character == '.'
                || character == Path.DirectorySeparatorChar
                || character == Path.AltDirectorySeparatorChar
                || IsInvalid(character)
                || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException(
                "A managed extension must contain extension text only, without dots, separators, whitespace, or invalid filename characters.",
                nameof(extension));
        }

        return normalized.ToLowerInvariant();
    }

    private static bool IsInvalid(char character) =>
        character <= ''
        || character == ''
        || InvalidFileNameCharacters.Contains(character);

    private static string TrimPhysicalEnding(string value) => value.Trim().TrimEnd('.', ' ');

    private static bool IsReservedDeviceBaseName(string value)
    {
        var dotIndex = value.IndexOf('.', StringComparison.Ordinal);
        var baseName = (dotIndex < 0 ? value : value[..dotIndex]).TrimEnd('.', ' ');
        return ReservedDeviceNames.Contains(baseName);
    }

    private static string TruncateTextElements(string value, int maximumTextElements)
    {
        var starts = StringInfo.ParseCombiningCharacters(value);
        return starts.Length <= maximumTextElements
            ? value
            : value[..starts[maximumTextElements]];
    }

    private static HashSet<string> CreateReservedDeviceNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON",
            "PRN",
            "AUX",
            "NUL",
        };

        for (var index = 1; index <= 9; index++)
        {
            names.Add($"COM{index}");
            names.Add($"LPT{index}");
        }

        return names;
    }
}
