using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Database;

/// <summary>
/// Persistence-facing shorthand for domain time. It owns no rules of its own: every conversion
/// delegates to <see cref="DomainTime"/>, the single authority for the UTC epoch-millisecond
/// persistence convention (44.2.2).
/// </summary>
public static class DbTime
{
    public static long Format(DateTimeOffset value) => DomainTime.ToUnixMilliseconds(value);

    public static long? FormatOrNull(DateTimeOffset? value) => DomainTime.ToUnixMillisecondsOrNull(value);

    public static DateTimeOffset Parse(long value) => DomainTime.FromUnixMilliseconds(value);

    public static DateTimeOffset? ParseOrNull(long? value) => DomainTime.FromUnixMillisecondsOrNull(value);
}
