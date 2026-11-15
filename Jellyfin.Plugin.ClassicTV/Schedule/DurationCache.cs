using System.Collections.Concurrent;

namespace Jellyfin.Plugin.ClassicTV.Schedule;

/// <summary>
/// Thread-safe in-memory cache of resolved episode durations in seconds.
///
/// Populated by <see cref="RefreshDurationsTask"/> after Jellyfin's library is ready.
/// Read by <see cref="ScheduleCacheWorker"/> on each cache-build cycle.
///
/// Entries never expire — durations don't change unless the library is re-scanned,
/// at which point <see cref="RefreshDurationsTask"/> runs again and overwrites them.
/// </summary>
public sealed class DurationCache
{
    private readonly ConcurrentDictionary<string, double> _durations = new();
    private readonly ConcurrentDictionary<string, string?> _ratings = new();

    /// <summary>Store the duration in seconds for a Jellyfin item ID.</summary>
    public void Set(string jellyfinId, double durationSeconds)
        => _durations[jellyfinId] = durationSeconds;

    /// <summary>Store the resolved OfficialRating for a Jellyfin item ID.</summary>
    public void SetRating(string jellyfinId, string? officialRating)
        => _ratings[jellyfinId] = officialRating;

    /// <summary>
    /// Return the stored duration for an ID, or <see cref="ScheduleEngine.FallbackDurationSeconds"/>
    /// if the ID has not been resolved yet.
    /// </summary>
    public double GetOrFallback(string jellyfinId)
        => _durations.TryGetValue(jellyfinId, out var d) ? d : ScheduleEngine.FallbackDurationSeconds;

    /// <summary>
    /// A snapshot of the current cache as a read-only dictionary, suitable for
    /// passing directly to <see cref="ScheduleEngine.ComputeSchedule"/>.
    /// </summary>
    public IReadOnlyDictionary<string, double> AsReadOnlyDictionary()
        => _durations;

    /// <summary>A snapshot of resolved item ratings keyed by Jellyfin ID.</summary>
    public IReadOnlyDictionary<string, string?> AsRatingsReadOnlyDictionary()
        => _ratings;

    /// <summary>Total number of resolved entries.</summary>
    public int Count => _durations.Count;
}
