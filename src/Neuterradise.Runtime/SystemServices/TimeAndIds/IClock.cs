namespace Neuterradise.App.SystemServices.TimeAndIds;

/// <summary>
/// Canonical conversion rules for persisted domain time.
///
/// Policy (44.2.2): every persisted domain timestamp is UTC epoch milliseconds in a signed 64-bit
/// integer, produced by the centralized clock. Local time is never used for ordering, comparison or
/// persistence; a local representation exists only at the presentation edge.
/// </summary>
public static class DomainTime
{
    /// <summary>Epoch milliseconds for the Unix epoch itself, the lower bound of a sane timestamp.</summary>
    public const long UnixEpochMilliseconds = 0;

    /// <summary>
    /// Converts an instant to canonical persisted milliseconds. The value is normalized to UTC first,
    /// so an offset-carrying value can never persist a local-time reading.
    /// </summary>
    public static long ToUnixMilliseconds(DateTimeOffset value) =>
        value.ToUniversalTime().ToUnixTimeMilliseconds();

    public static long? ToUnixMillisecondsOrNull(DateTimeOffset? value) =>
        value is { } instant ? ToUnixMilliseconds(instant) : null;

    /// <summary>
    /// Reads canonical persisted milliseconds back as a UTC instant.
    /// </summary>
    public static DateTimeOffset FromUnixMilliseconds(long value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value).ToUniversalTime();

    public static DateTimeOffset? FromUnixMillisecondsOrNull(long? value) =>
        value is { } milliseconds ? FromUnixMilliseconds(milliseconds) : null;

    /// <summary>
    /// True when the value is inside the range <see cref="DateTimeOffset.FromUnixTimeMilliseconds"/>
    /// can represent, so a corrupt persisted number is rejected instead of throwing deep in a read.
    /// </summary>
    public static bool IsRepresentable(long value) =>
        value >= DateTimeOffset.MinValue.ToUnixTimeMilliseconds()
        && value <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    /// <summary>
    /// Converts a UTC instant to the user's local time for display only. Persistence and ordering
    /// never call this.
    /// </summary>
    public static DateTimeOffset ToLocalForDisplay(DateTimeOffset utcValue) => utcValue.ToLocalTime();
}

/// <summary>
/// The application-facing clock. Every domain timestamp comes from an <see cref="IClock"/> or from a
/// <see cref="TimeProvider"/> bridged onto one, so no component reads the ambient system clock
/// directly and time can be controlled at one composition point.
/// </summary>
public interface IClock
{
    /// <summary>The current instant in UTC.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>The current instant as canonical persisted epoch milliseconds.</summary>
    long UtcNowUnixMilliseconds => DomainTime.ToUnixMilliseconds(UtcNow);
}

/// <summary>
/// The production clock. It delegates to <see cref="TimeProvider.System"/> so the application has one
/// time primitive rather than two independent sources of "now".
/// </summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => TimeProvider.System.GetUtcNow();
}

/// <summary>
/// Bridges an explicit <see cref="TimeProvider"/> onto <see cref="IClock"/>. Components that already
/// receive a TimeProvider and components that receive an IClock therefore share the same instant.
/// </summary>
public sealed class TimeProviderClock : IClock
{
    private readonly TimeProvider _timeProvider;

    public TimeProviderClock(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();
}

public static class ClockExtensions
{
    /// <summary>
    /// The current instant as canonical persisted epoch milliseconds, for the many call sites that
    /// hold a <see cref="TimeProvider"/> rather than an <see cref="IClock"/>.
    /// </summary>
    public static long GetUtcNowUnixMilliseconds(this TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return DomainTime.ToUnixMilliseconds(timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Views a <see cref="TimeProvider"/> as the application clock contract.
    /// </summary>
    public static IClock AsClock(this TimeProvider timeProvider) => new TimeProviderClock(timeProvider);
}
