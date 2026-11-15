using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ClassicTV.Schedule;

/// <summary>
/// Background service that pre-computes and caches schedule slots for the next 48 hours.
///
/// Reads episode durations from <see cref="DurationCache"/>, which is populated
/// separately by <see cref="RefreshDurationsTask"/> after the library is ready.
/// Falls back to <see cref="ScheduleEngine.FallbackDurationSeconds"/> for any
/// episode whose duration has not yet been resolved.
///
/// STARTUP SAFETY:
/// This worker does not call ILibraryManager or any Jellyfin library service.
/// All library access happens in RefreshDurationsTask (IScheduledTask), which
/// Jellyfin only runs after the server is fully initialised — no race condition.
///
/// NOTE: IBasePlugin and IHostedService cannot be on the same class.
/// This service is registered separately via <see cref="PluginServiceRegistrator"/>.
/// </summary>
public sealed class ScheduleCacheWorker : IHostedService, IDisposable
{
    private readonly ScheduleCache _cache;
    private readonly DurationCache _durationCache;
    private readonly ILogger<ScheduleCacheWorker> _logger;
    private readonly SemaphoreSlim _rebuildSemaphore = new SemaphoreSlim(1, 1);
    private Timer? _timer;

    internal const int LookaheadHours = 48;
    internal const int RefreshIntervalHours = 1;

    // 5 seconds — safe because we make no library calls; just schedule arithmetic.
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);

    public ScheduleCacheWorker(
        ScheduleCache cache,
        DurationCache durationCache,
        ILogger<ScheduleCacheWorker> logger)
    {
        _cache = cache;
        _durationCache = durationCache;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "ClassicTV ScheduleCacheWorker starting (first run in {Delay}s)",
            InitialDelay.TotalSeconds);

        _timer = new Timer(
            OnTimer,
            null,
            dueTime: InitialDelay,
            period: TimeSpan.FromHours(RefreshIntervalHours));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ClassicTV ScheduleCacheWorker stopping");
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        _rebuildSemaphore.Dispose();
    }

    /// <summary>
    /// Triggers an immediate cache rebuild outside of the normal hourly schedule.
    /// Called by <see cref="RefreshDurationsTask"/> after resolving new durations
    /// so the schedule cache reflects real episode runtimes without waiting up to
    /// an hour for the next timer cycle.
    /// Also exposed for testing.
    /// </summary>
    internal async Task TriggerRebuildAsync(CancellationToken ct = default)
    {
        await _rebuildSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PopulateCache();
        }
        finally
        {
            _rebuildSemaphore.Release();
        }
    }

    /// <summary>
    /// Timer callback. Skips the rebuild if one is already in progress rather than
    /// stacking a concurrent execution.
    /// </summary>
    private async void OnTimer(object? _)
    {
        if (!await _rebuildSemaphore.WaitAsync(0).ConfigureAwait(false))
        {
            _logger.LogDebug("ClassicTV: cache rebuild already in progress, skipping timer tick");
            return;
        }
        try
        {
            PopulateCache();
        }
        finally
        {
            _rebuildSemaphore.Release();
        }
    }

    // ---------------------------------------------------------------------------
    // Cache population — no Jellyfin library calls; pure schedule arithmetic.
    // ---------------------------------------------------------------------------

    private void PopulateCache()
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                _logger.LogWarning("ClassicTV: plugin configuration not available, skipping cache build");
                return;
            }

            var root = config.ParsedChannels;
            if (root is null || root.Channels.Count == 0)
            {
                _logger.LogWarning("ClassicTV: no channels configured");
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long end = now + (LookaheadHours * 3600);

            _cache.EvictBefore(now - 3600);

            // Use whatever durations RefreshDurationsTask has resolved.
            // Falls back to FallbackDurationSeconds for unresolved IDs.
            var durations = _durationCache.AsReadOnlyDictionary();

            foreach (var channel in root.Channels)
            {
                if (!channel.Enabled)
                {
                    _logger.LogDebug("ClassicTV: skipping disabled channel {ChannelId}", channel.Id);
                    continue;
                }

                if (channel.Guide)
                {
                    _logger.LogDebug("ClassicTV: skipping guide channel {ChannelId}", channel.Id);
                    continue;
                }

                // Never schedule episodes that are no longer in the library: a dead
                // ID would be served and the client would 404 on it repeatedly.
                var schedulable = ScheduleEngine.PruneMissingEpisodes(channel, durations);
                schedulable = ScheduleEngine.PruneByRating(schedulable, _durationCache.AsRatingsReadOnlyDictionary());

                int populated = 0;
                for (long t = now; t < end; t += 60)
                {
                    var result = ScheduleEngine.ComputeSchedule(schedulable, t, durations);
                    _cache.Set(channel.Id, t, result);
                    populated++;
                }

                _logger.LogDebug(
                    "ClassicTV: populated {Count} slots for channel {ChannelId} ({Resolved} durations resolved)",
                    populated, channel.Id, _durationCache.Count);
            }

            _logger.LogInformation(
                "ClassicTV: cache build complete. {TotalSlots} total slots across {ChannelCount} channels",
                _cache.Count, root.Channels.Count);
        }
        catch (Exception ex)
        {
            // Never let an exception escape a timer callback.
            _logger.LogError(ex, "ClassicTV: unhandled exception during cache build");
        }
    }
}
