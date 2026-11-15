using System.Collections.Concurrent;

namespace Jellyfin.Plugin.ClassicTV.Schedule;

/// <summary>
/// Thread-safe in-memory cache of pre-computed schedule slots.
///
/// The background worker (<see cref="ScheduleCacheWorker"/>) populates this cache
/// for the next 48 hours. On-demand requests from the controller are cache lookups,
/// not real-time calculations.
///
/// Cache keys are (channelId, unixMinute) where unixMinute = unixSeconds / 60.
/// </summary>
public sealed class ScheduleCache
{
    // Key: (channelId, unixMinute)
    private readonly ConcurrentDictionary<(string, long), ScheduleResult> _slots = new();

    /// <summary>Store a result for a channel at the given unix timestamp (rounded to minute).</summary>
    public void Set(string channelId, long unixSeconds, ScheduleResult result)
    {
        long key = unixSeconds / 60;
        _slots[(channelId, key)] = result;
    }

    /// <summary>
    /// Retrieve a cached result. Returns null on cache miss.
    /// The timestamp is rounded down to the nearest minute for lookup.
    /// </summary>
    public ScheduleResult? Get(string channelId, long unixSeconds)
    {
        long key = unixSeconds / 60;
        return _slots.TryGetValue((channelId, key), out var result) ? result : null;
    }

    /// <summary>Evict all entries older than the given unix timestamp.</summary>
    public void EvictBefore(long unixSeconds)
    {
        long cutoffKey = unixSeconds / 60;
        foreach (var k in _slots.Keys)
            if (k.Item2 < cutoffKey)
                _slots.TryRemove(k, out _);
    }

    /// <summary>Total number of cached slots (for diagnostics).</summary>
    public int Count => _slots.Count;
}
