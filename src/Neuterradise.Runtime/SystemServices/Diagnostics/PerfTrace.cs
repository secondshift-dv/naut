using System.Collections.Concurrent;
using System.Diagnostics;

namespace Neuterradise.App.SystemServices.Diagnostics;

/// <summary>
/// Lightweight timing for app-owned work (route creation, key queries, activity polls, image loads).
/// Every measurement is aggregated in memory; anything slower than its budget is also written to the
/// trace log, so a recurring stall is visible in diagnostics instead of being a vague "feels laggy".
/// </summary>
public static class PerfTrace
{
    private static readonly ConcurrentDictionary<string, PerfStat> _stats = new(StringComparer.Ordinal);

    public static Scope Measure(string name, double budgetMilliseconds = 100) => new(name, budgetMilliseconds);

    public static void Record(string name, double elapsedMilliseconds, double budgetMilliseconds = 100)
    {
        var stat = _stats.GetOrAdd(name, static _ => new PerfStat());
        stat.Add(elapsedMilliseconds);

        if (elapsedMilliseconds > budgetMilliseconds)
        {
            Trace.TraceWarning("PERF {0} took {1:F1} ms (budget {2:F0} ms)", name, elapsedMilliseconds, budgetMilliseconds);
        }
    }

    public static IReadOnlyDictionary<string, PerfSnapshot> Snapshot() =>
        _stats.ToDictionary(pair => pair.Key, pair => pair.Value.ToSnapshot(), StringComparer.Ordinal);

    public static void Reset() => _stats.Clear();

    public readonly struct Scope : IDisposable
    {
        private readonly string _name;
        private readonly double _budget;
        private readonly long _started;

        internal Scope(string name, double budget)
        {
            _name = name;
            _budget = budget;
            _started = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_name is not null)
            {
                Record(_name, Stopwatch.GetElapsedTime(_started).TotalMilliseconds, _budget);
            }
        }
    }

    private sealed class PerfStat
    {
        private readonly Lock _sync = new();
        private long _count;
        private double _total;
        private double _max;

        public void Add(double value)
        {
            lock (_sync)
            {
                _count++;
                _total += value;
                _max = Math.Max(_max, value);
            }
        }

        public PerfSnapshot ToSnapshot()
        {
            lock (_sync)
            {
                return new PerfSnapshot(_count, _count == 0 ? 0 : _total / _count, _max);
            }
        }
    }
}

public readonly record struct PerfSnapshot(long Count, double MeanMilliseconds, double MaxMilliseconds);
