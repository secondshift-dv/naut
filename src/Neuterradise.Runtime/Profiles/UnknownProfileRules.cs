using System.Globalization;

namespace Neuterradise.App.Profiles;

public static class UnknownProfileRules
{
    private const string _labelPrefix = "Unknown ";

    public static string FormatDerivedLabel(long unknownSequence)
    {
        ValidateSequence(unknownSequence);
        return string.Create(CultureInfo.InvariantCulture, $"{_labelPrefix}{unknownSequence}");
    }

    public static bool TryParseDerivedLabel(string? label, out long unknownSequence)
    {
        unknownSequence = 0;
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        var trimmed = label.Trim();
        if (!trimmed.StartsWith(_labelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var numberPart = trimmed.AsSpan(_labelPrefix.Length).Trim();
        if (long.TryParse(numberPart, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            unknownSequence = parsed;
            return true;
        }

        return false;
    }

    public static bool IsActiveUnresolved(long activeOwnedMediaCount) => activeOwnedMediaCount > 0;

    public static bool LeavesActiveUnresolved(long activeOwnedMediaCount) => activeOwnedMediaCount <= 0;

    public static void ValidateSequence(long unknownSequence)
    {
        if (unknownSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(unknownSequence),
                unknownSequence,
                "UnknownSequence must be a positive integer (> 0).");
        }
    }
}
