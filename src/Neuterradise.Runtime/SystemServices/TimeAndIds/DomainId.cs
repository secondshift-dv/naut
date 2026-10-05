namespace Neuterradise.App.SystemServices.TimeAndIds;

/// <summary>
/// The domain role an identifier plays. The kind is carried through parsing and validation so a
/// rejected value names the identifier it belongs to instead of producing an anonymous GUID error.
/// </summary>
public enum DomainIdKind
{
    Profile,
    Media,
    MediaComponent,
    Identity,
    IdentitySample,
    Face,
    ImportSession,
    ImportUnit,
    ImportItem,
    Job,
    Operation,
    TrashEntry,
    Activity,
    Relation,
}

/// <summary>
/// Single canonical authority for domain identifier values and their text form.
///
/// Rules (44.2.2): a domain identifier is a nonempty GUID; its persisted TEXT form is the lowercase
/// standard "D" format; ID8 is the first 8 characters of the lowercase "N" format; and every pairwise
/// comparison uses the "D" form with ordinal comparison. No other component may invent a second text
/// form for the same identifier.
/// </summary>
public static class DomainId
{
    /// <summary>Length of the canonical "D" text form, including the four hyphens.</summary>
    public const int CanonicalTextLength = 36;

    /// <summary>Number of characters in the short human-facing ID8 form.</summary>
    public const int ShortIdLength = 8;

    /// <summary>
    /// Creates a new domain identifier. Callers never construct identifiers from user text.
    /// </summary>
    public static Guid New() => Guid.NewGuid();

    /// <summary>
    /// Canonical persisted text: lowercase standard "D" format. <see cref="Guid.Empty"/> is rejected
    /// because an empty identifier is never a valid domain value.
    /// </summary>
    public static string Format(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An empty GUID is not a valid domain identifier.", nameof(value));
        }

        return value.ToString("D").ToLowerInvariant();
    }

    /// <summary>
    /// Canonical text for a nullable identifier, or null when there is genuinely no identifier.
    /// </summary>
    public static string? FormatOrNull(Guid? value) => value is { } id ? Format(id) : null;

    /// <summary>
    /// The short human-facing form used in managed directory and file names: the first eight
    /// characters of the lowercase "N" format. It is a display/naming affordance derived from the
    /// identifier, never a replacement for it and never parsed back into one.
    /// </summary>
    public static string Id8(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An empty GUID has no short identifier form.", nameof(value));
        }

        return value.ToString("N").ToLowerInvariant()[..ShortIdLength];
    }

    /// <summary>
    /// Strict canonical parse: the text must already be the lowercase "D" form of a nonempty GUID.
    /// Braced, parenthesised, uppercase and hyphenless spellings are rejected so that a
    /// noncanonical value can never enter the domain through a read path.
    /// </summary>
    public static bool TryParse(string? text, out Guid value)
    {
        value = Guid.Empty;

        if (text is not { Length: CanonicalTextLength })
        {
            return false;
        }

        if (!Guid.TryParseExact(text, "D", out var parsed) || parsed == Guid.Empty)
        {
            return false;
        }

        if (!string.Equals(text, parsed.ToString("D").ToLowerInvariant(), StringComparison.Ordinal))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>
    /// Strict canonical parse for call sites that do not know which identifier the text holds.
    /// </summary>
    public static Guid Parse(string? text)
    {
        if (!TryParse(text, out var value))
        {
            throw new FormatException(
                $"'{text ?? "(null)"}' is not a canonical lowercase domain identifier.");
        }

        return value;
    }

    /// <summary>
    /// Strict canonical parse that names the identifier kind when the value is not canonical.
    /// </summary>
    public static Guid Parse(string? text, DomainIdKind kind)
    {
        if (!TryParse(text, out var value))
        {
            throw new FormatException(
                $"'{text ?? "(null)"}' is not a canonical lowercase {kind} identifier.");
        }

        return value;
    }

    /// <summary>
    /// Parses an optional persisted identifier. Null and empty text mean "no identifier"; any other
    /// noncanonical text is an error rather than a silently dropped value.
    /// </summary>
    public static Guid? ParseOrNull(string? text, DomainIdKind kind)
        => string.IsNullOrEmpty(text) ? null : Parse(text, kind);

    /// <summary>
    /// Validates an identifier supplied by a caller, naming the parameter and the kind on failure.
    /// </summary>
    public static Guid Require(Guid value, DomainIdKind kind, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException($"A {kind} identifier is required.", parameterName);
        }

        return value;
    }

    /// <summary>
    /// Canonical pairwise comparison: ordinal comparison of the "D" text form.
    /// </summary>
    public static bool AreSame(Guid left, Guid right)
        => string.Equals(
            left.ToString("D"),
            right.ToString("D"),
            StringComparison.Ordinal);

    /// <summary>
    /// Ordinal comparer over the canonical text form, for deterministic ordering of identifier lists.
    /// </summary>
    public static int CompareCanonical(Guid left, Guid right)
        => string.CompareOrdinal(left.ToString("D"), right.ToString("D"));
}
