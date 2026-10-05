using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Jobs;

public sealed class JobProgressHub
{
    public const double DefaultMaxUpdatesPerSecond = 10;

    private readonly IClock _clock;
    private readonly Func<JobProgressSnapshot, CancellationToken, Task> _onPublish;
    private readonly TimeSpan _minimumInterval;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, JobProgressSnapshot> _latest = new();
    private readonly DateTimeOffset _createdUtc;
    private DateTimeOffset _lastPublishUtc;
    private long _publishedAggregates;

    public JobProgressHub(
        IClock clock,
        Func<JobProgressSnapshot, CancellationToken, Task> onPublish,
        double maxUpdatesPerSecond = DefaultMaxUpdatesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(onPublish);
        if (maxUpdatesPerSecond <= 0
            || double.IsNaN(maxUpdatesPerSecond)
            || double.IsInfinity(maxUpdatesPerSecond))
        {
            throw new ArgumentOutOfRangeException(nameof(maxUpdatesPerSecond));
        }

        _clock = clock;
        _onPublish = onPublish;
        _minimumInterval = TimeSpan.FromSeconds(1.0 / maxUpdatesPerSecond);
        _createdUtc = clock.UtcNow;
        _lastPublishUtc = _createdUtc;
    }

    public void Report(Guid jobId, long completed, long total, string? stage, JobState state = JobState.Running)
    {
        if (completed < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completed), "Progress cannot be negative.");
        }

        if (total < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(total), "Progress total cannot be negative.");
        }

        if (completed > total)
        {
            throw new ArgumentOutOfRangeException(nameof(completed), "Progress cannot exceed the aggregate total.");
        }

        lock (_gate)
        {
            _latest[jobId] = new JobProgressSnapshot(jobId, completed, total, stage, state);
        }
    }

    public async Task PublishDueAsync(CancellationToken cancellationToken = default)
    {
        JobProgressSnapshot[] due;
        lock (_gate)
        {
            if (_latest.Count == 0)
            {
                return;
            }

            if (_clock.UtcNow - _lastPublishUtc < _minimumInterval)
            {
                return;
            }

            due = _latest.Values.ToArray();
            _latest.Clear();
            _lastPublishUtc = _clock.UtcNow;
        }

        foreach (var snapshot in due)
        {
            await _onPublish(snapshot, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _publishedAggregates);
        }
    }

    public long PublishedAggregateCount => Interlocked.Read(ref _publishedAggregates);

    public double MaxUpdatesPerSecond => 1.0 / _minimumInterval.TotalSeconds;

    public double ObservedPublishesPerSecond
    {
        get
        {
            var elapsed = (_clock.UtcNow - _createdUtc).TotalSeconds;
            return elapsed > 0 ? PublishedAggregateCount / elapsed : 0;
        }
    }

    public int PendingAggregateCount
    {
        get
        {
            lock (_gate)
            {
                return _latest.Count;
            }
        }
    }

    public async Task<long> FlushPendingAsync(CancellationToken cancellationToken = default)
    {
        JobProgressSnapshot[] pending;
        lock (_gate)
        {
            if (_latest.Count == 0)
            {
                return 0;
            }

            pending = _latest.Values.ToArray();
            _latest.Clear();
            _lastPublishUtc = _clock.UtcNow;
        }

        foreach (var snapshot in pending)
        {
            await _onPublish(snapshot, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _publishedAggregates);
        }

        return pending.Length;
    }

    public JobProgressSnapshot? GetLatest(Guid jobId)
    {
        lock (_gate)
        {
            return _latest.TryGetValue(jobId, out var snapshot) ? snapshot : null;
        }
    }
}
