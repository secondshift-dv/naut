using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Database;

/// <summary>
/// Persistence-facing shorthand for domain identifier text. It owns no rules of its own: every
/// format and parse call delegates to <see cref="DomainId"/>, which is the single canonical
/// authority for identifier text (44.2.2). Read paths that know which identifier a column holds
/// should pass the <see cref="DomainIdKind"/> overloads so a rejected value names the identifier.
/// </summary>
public static class DbGuid
{
    public static string Format(Guid value) => DomainId.Format(value);

    public static string? FormatOrNull(Guid? value) => DomainId.FormatOrNull(value);

    public static Guid Parse(string value) => DomainId.Parse(value);

    public static Guid Parse(string value, DomainIdKind kind) => DomainId.Parse(value, kind);

    public static Guid? ParseOrNull(string? value, DomainIdKind kind) => DomainId.ParseOrNull(value, kind);

    public static bool TryParse(string? value, out Guid parsed) => DomainId.TryParse(value, out parsed);
}
