using System.Text;

namespace Neuterradise.App.Settings;

public static class TaxonomyNamePolicy
{

    public const int MaximumLength = 64;

    public static string? TryNormalize(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var collapsed = CollapseWhitespace(name);
        if (collapsed.Length == 0)
        {
            return null;
        }

        var canonical = collapsed.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        return canonical.Length is 0 or > MaximumLength ? null : canonical;
    }

    public static string? TryNormalizeDisplayName(string? name)
    {
        var collapsed = name is null ? string.Empty : CollapseWhitespace(name);
        if (collapsed.Length == 0)
        {
            return null;
        }

        var display = collapsed.Normalize(NormalizationForm.FormKC);
        return display.Length > MaximumLength ? null : display;
    }

    public static bool AreSameName(string? left, string? right)
    {
        var leftKey = TryNormalize(left);
        return leftKey is not null && string.Equals(leftKey, TryNormalize(right), StringComparison.Ordinal);
    }

    public static string Normalize(string? name) =>
        TryNormalize(name)
        ?? throw new ArgumentException(
            $"'{name}' is not a usable Category or Tag name: it must contain at least one visible character and at most {MaximumLength} after normalization.",
            nameof(name));

    public static string NormalizeDisplayName(string? name) =>
        TryNormalizeDisplayName(name)
        ?? throw new ArgumentException(
            $"'{name}' is not a usable Category or Tag name.",
            nameof(name));

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    // ── K01: Tag input tokenization ───────────────────────────────────────────

    public sealed record TaxonomyTagToken(string CanonicalName, string DisplayName);

    /// <summary>
    /// Parses tag input text by splitting on comma, CR, and LF.
    /// Returns normalized tokens suitable for Import, Profile, and Settings.
    /// Deduplicates by canonical key, preserving first display spelling.
    /// </summary>
    public static IReadOnlyList<TaxonomyTagToken> ParseTagTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var result = new List<TaxonomyTagToken>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in SplitTagInput(text))
        {
            var canonical = TryNormalize(raw);
            if (canonical is null)
            {
                continue;
            }

            if (!seen.Add(canonical))
            {
                continue;
            }

            var display = TryNormalizeDisplayName(raw) ?? canonical;
            result.Add(new TaxonomyTagToken(canonical, display));
        }

        return result;
    }

    /// <summary>
    /// Parses partial tag input for live TextBox usage.
    /// Returns completed tokens and the remaining partial search text.
    /// </summary>
    public static (IReadOnlyList<TaxonomyTagToken> Committed, string? Remaining) TryParsePartialTagInput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ([], null);
        }

        var committed = new List<TaxonomyTagToken>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var segments = SplitTagInputSegments(text);
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var isLast = i == segments.Count - 1;
            var endsWithDelimiter = !isLast || text.EndsWith(",", StringComparison.Ordinal)
                || text.EndsWith("\n", StringComparison.Ordinal)
                || text.EndsWith("\r", StringComparison.Ordinal);

            if (isLast && !endsWithDelimiter)
            {
                // Last segment without trailing delimiter is partial.
                var remaining = string.IsNullOrWhiteSpace(segment) ? null : segment.TrimStart();
                return (committed, remaining);
            }

            var canonical = TryNormalize(segment);
            if (canonical is null)
            {
                continue;
            }

            if (!seen.Add(canonical))
            {
                continue;
            }

            var display = TryNormalizeDisplayName(segment) ?? canonical;
            committed.Add(new TaxonomyTagToken(canonical, display));
        }

        return (committed, null);
    }

    private static List<string> SplitTagInputSegments(string text)
    {
        var segments = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            if (ch is ',' or '\n' or '\r')
            {
                segments.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }
        segments.Add(current.ToString());
        return segments;
    }

    private static IEnumerable<string> SplitTagInput(string text)
    {
        foreach (var segment in SplitTagInputSegments(text))
        {
            if (!string.IsNullOrWhiteSpace(segment))
            {
                yield return segment;
            }
        }
    }
}
