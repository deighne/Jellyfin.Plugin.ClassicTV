using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ClassicTV.Schedule;

/// <summary>
/// Jellyfin scheduled task that resolves actual episode durations from the library
/// and stores them in <see cref="DurationCache"/>.
///
/// Uses <see cref="IScheduledTask"/> rather than <see cref="Microsoft.Extensions.Hosting.IHostedService"/>
/// because Jellyfin's task scheduler guarantees the library is fully initialised
/// before ExecuteAsync is called — unlike IHostedService which starts during host
/// initialisation when ILibraryManager may not be ready.
///
/// Jellyfin discovers IScheduledTask implementations automatically via assembly
/// scanning. No DI registration is required.
/// </summary>
public sealed class RefreshDurationsTask : IScheduledTask
{
    private readonly DurationCache _durationCache;
    private readonly ScheduleCacheWorker _cacheWorker;
    private readonly ILogger<RefreshDurationsTask> _logger;
    private readonly ILibraryManager? _libraryManager;

    /// <summary>
    /// Production constructor — called by Jellyfin's DI with a live ILibraryManager.
    /// </summary>
    public RefreshDurationsTask(
        DurationCache durationCache,
        ScheduleCacheWorker cacheWorker,
        ILibraryManager libraryManager,
        ILogger<RefreshDurationsTask> logger)
    {
        _durationCache = durationCache;
        _cacheWorker = cacheWorker;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Test constructor — no ILibraryManager or ScheduleCacheWorker, safe without a Jellyfin host.
    /// </summary>
    internal RefreshDurationsTask(
        DurationCache durationCache,
        ILogger<RefreshDurationsTask> logger)
    {
        _durationCache = durationCache;
        _cacheWorker = null!;
        _libraryManager = null;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Key => "ClassicTVRefreshDurations";

    /// <inheritdoc />
    public string Name => "Refresh ClassicTV episode durations";

    /// <inheritdoc />
    public string Category => "ClassicTV";

    /// <inheritdoc />
    public string Description => "Resolves actual episode durations from the Jellyfin library for use in Classic TV schedule calculation. Run this after adding or removing episodes from Classic TV channels.";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Run once on every Jellyfin start. DurationCache is in-memory and begins
        // empty after a restart, so without this the schedule engine would use the
        // 1800s fallback duration until the daily run — producing a schedule that
        // silently shifts once real durations resolve.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.StartupTrigger
        };

        // Then daily at 3am so durations stay fresh after library scans.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                _logger.LogWarning("ClassicTV RefreshDurationsTask: plugin configuration not available, skipping");
                return;
            }

            var root = config.ParsedChannels;
            if (root is null || root.Channels.Count == 0)
            {
                _logger.LogWarning("ClassicTV RefreshDurationsTask: no channels configured, skipping");
                return;
            }

            if (_libraryManager is null)
            {
                _logger.LogWarning("ClassicTV RefreshDurationsTask: no library manager available (test mode), skipping");
                return;
            }

            // Collect all unique Jellyfin IDs referenced across all channels.
            var allIds = new HashSet<string>();
            foreach (var channel in root.Channels)
                foreach (var block in channel.Blocks)
                    if (!block.Offline && block.Shows is not null)
                        foreach (var show in block.Shows)
                            foreach (var id in show.JellyfinIds)
                                allIds.Add(id);

            if (allIds.Count == 0)
            {
                _logger.LogInformation("ClassicTV RefreshDurationsTask: no Jellyfin IDs found in channel config");
                progress.Report(100);
                return;
            }

            _logger.LogInformation("ClassicTV RefreshDurationsTask: resolving durations for {Count} items", allIds.Count);

            int resolved = 0;
            int total = allIds.Count;
            int index = 0;

            foreach (var idStr in allIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (Guid.TryParse(idStr, out var guid))
                {
                    try
                    {
                        var item = _libraryManager.GetItemById(guid);
                        if (item?.RunTimeTicks is long ticks && ticks > 0)
                        {
                            _durationCache.Set(idStr, ticks / 10_000_000.0);
                            resolved++;
                        }
                        if (item is not null)
                        {
                            var rating = item.OfficialRating;
                            if (string.IsNullOrWhiteSpace(rating)
                                && item is MediaBrowser.Controller.Entities.TV.Episode ep
                                && ep.SeriesId != Guid.Empty)
                            {
                                rating = _libraryManager.GetItemById(ep.SeriesId)?.OfficialRating;
                            }
                            _durationCache.SetRating(idStr, rating);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "ClassicTV RefreshDurationsTask: failed to get duration for {Id}", idStr);
                    }
                }

                index++;
                progress.Report((double)index / total * 100);
            }

            _logger.LogInformation(
                "ClassicTV RefreshDurationsTask: resolved {Resolved}/{Total} durations",
                resolved, total);

            progress.Report(100);

            // Rebuild the schedule cache immediately so the new durations take effect
            // without waiting up to an hour for the next ScheduleCacheWorker cycle.
            if (_cacheWorker is not null)
            {
                _logger.LogInformation("ClassicTV RefreshDurationsTask: triggering schedule cache rebuild");
                await _cacheWorker.TriggerRebuildAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("ClassicTV RefreshDurationsTask: cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ClassicTV RefreshDurationsTask: unhandled exception");
        }
    }
}
