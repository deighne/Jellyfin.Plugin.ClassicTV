using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ClassicTV.Schedule;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ClassicTV.Api;

/// <summary>
/// REST controller for the Classic TV management API.
///
/// All endpoints require admin elevation — this controller is never called by the Kodi
/// addon and must not be accessible anonymously.
///
/// Routes:
///   GET  /ClassicTV/manage/channels                         — full channel config (incl. disabled)
///   POST /ClassicTV/manage/channels                         — replace full channel config
///   GET  /ClassicTV/manage/library/series                   — list all series in the library
///   GET  /ClassicTV/manage/library/series/{id}/episodes     — list episodes for a series
/// </summary>
[ApiController]
[Route("ClassicTV/manage")]
[Authorize(Policy = "RequiresElevation")]
public sealed class ManagementController : ControllerBase
{
    private readonly ScheduleCacheWorker _cacheWorker;
    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<ManagementController> _logger;

    public ManagementController(
        ScheduleCacheWorker cacheWorker,
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        ILogger<ManagementController> logger)
    {
        _cacheWorker = cacheWorker;
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // GET /ClassicTV/manage/channels
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the full channel configuration including disabled channels and all
    /// management fields. Intended for the dashboard config page only.
    /// </summary>
    [HttpGet("channels")]
    [ProducesResponseType(typeof(ChannelConfigRoot), 200)]
    [ProducesResponseType(503)]
    public ActionResult<ChannelConfigRoot> GetManageChannels()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            _logger.LogError("ClassicTV ManagementController: Plugin.Instance is null");
            return StatusCode(503, "Plugin not initialised");
        }

        var root = plugin.Configuration.ParsedChannels;
        if (root is null)
        {
            _logger.LogError("ClassicTV ManagementController: channel configuration JSON is invalid");
            return StatusCode(503, "Channel configuration is invalid");
        }

        return Ok(root);
    }

    // -------------------------------------------------------------------------
    // POST /ClassicTV/manage/channels
    // -------------------------------------------------------------------------

    /// <summary>
    /// Replaces the full channel configuration. The body must be a valid
    /// <see cref="ChannelConfigRoot"/>. On success, queues <see cref="RefreshDurationsTask"/>
    /// to rebuild the schedule cache with the new config.
    ///
    /// Returns 204 on success, 400 for bad input, 500 if the save or task queue fails.
    /// </summary>
    [HttpPost("channels")]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    [ProducesResponseType(503)]
    public IActionResult PostManageChannels([FromBody] ChannelConfigRoot body)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            _logger.LogError("ClassicTV ManagementController: Plugin.Instance is null on POST");
            return StatusCode(503, "Plugin not initialised");
        }

        // ---- Server-side validation ----
        // Client-side validation is not sufficient: any HTTP client can bypass it.
        // Validate IDs, numbers, block hour ranges, and block overlaps before persisting.
        var validationErrors = ChannelConfigValidator.Validate(body);
        if (validationErrors.Count > 0)
        {
            _logger.LogWarning(
                "ClassicTV ManagementController: POST /manage/channels rejected with {Count} validation error(s)",
                validationErrors.Count);
            return BadRequest(new ValidationErrorResponse { Errors = validationErrors });
        }

        // Serialize back to a canonical JSON string.
        // ASP.NET model binding has already deserialized the body — if we reach this
        // point the JSON was well-formed. Serialization failure here is highly unlikely
        // but handled defensively.
        string newJson;
        try
        {
            newJson = JsonSerializer.Serialize(body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ClassicTV ManagementController: failed to re-serialize incoming channel config");
            return BadRequest("Failed to serialize channel configuration");
        }

        // Snapshot for rollback if the task queue step fails.
        string previousJson = plugin.Configuration.ChannelsJson;

        // Persist new config.
        plugin.Configuration.ChannelsJson = newJson;
        plugin.SaveConfiguration();

        // Queue RefreshDurationsTask to rebuild the duration + schedule cache.
        // If this throws, we roll back the config so the server is not left in an
        // inconsistent state where the config is saved but the cache is stale.
        try
        {
            _taskManager.QueueScheduledTask<RefreshDurationsTask>();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "ClassicTV ManagementController: failed to queue RefreshDurationsTask — rolling back config");

            plugin.Configuration.ChannelsJson = previousJson;
            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception rollbackEx)
            {
                _logger.LogCritical(
                    rollbackEx,
                    "ClassicTV ManagementController: rollback SaveConfiguration ALSO failed — config state is uncertain");
            }

            return StatusCode(500, "Failed to trigger cache rebuild; configuration change rolled back");
        }

        _logger.LogInformation("ClassicTV ManagementController: channel config saved successfully");
        return NoContent(); // 204
    }

    // -------------------------------------------------------------------------
    // GET /ClassicTV/manage/library/series
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a sorted list of all TV series in the Jellyfin library, each with
    /// an episode count. Used by the dashboard series picker to populate the
    /// show selector when building channels.
    /// </summary>
    [HttpGet("library/series")]
    [ProducesResponseType(typeof(SeriesListResponse), 200)]
    public ActionResult<SeriesListResponse> GetSeries()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            Recursive = true,
            EnableTotalRecordCount = false
        };

        var items = _libraryManager.GetItemList(query);

        var series = items
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s =>
            {
                var epQuery = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    AncestorIds = new[] { s.Id },
                    Recursive = true,
                    EnableTotalRecordCount = false
                };
                int epCount = _libraryManager.GetCount(epQuery);

                return new SeriesItem
                {
                    Id = s.Id.ToString(),
                    Name = s.Name ?? string.Empty,
                    EpisodeCount = epCount,
                    Genres = s.Genres ?? Array.Empty<string>(),
                    OfficialRating = s.OfficialRating
                };
            })
            .ToList();

        _logger.LogInformation("ClassicTV ManagementController: returning {Count} series", series.Count);
        return Ok(new SeriesListResponse { Series = series });
    }

    // -------------------------------------------------------------------------
    // GET /ClassicTV/manage/library/movies
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a sorted list of all movies in the Jellyfin library so the
    /// dashboard picker can add movies to channels.
    /// </summary>
    [HttpGet("library/movies")]
    [ProducesResponseType(typeof(MovieListResponse), 200)]
    public ActionResult<MovieListResponse> GetMovies()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive = true,
            EnableTotalRecordCount = false
        };

        var items = _libraryManager.GetItemList(query);

        var movies = items
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => new MovieItem
            {
                Id = m.Id.ToString(),
                Name = m.Name ?? string.Empty,
                Year = m.ProductionYear,
                DurationSeconds = m.RunTimeTicks.HasValue ? m.RunTimeTicks.Value / 10_000_000.0 : 0.0,
                Genres = m.Genres ?? Array.Empty<string>(),
                OfficialRating = m.OfficialRating
            })
            .ToList();

        _logger.LogInformation("ClassicTV ManagementController: returning {Count} movies", movies.Count);
        return Ok(new MovieListResponse { Movies = movies });
    }

    // -------------------------------------------------------------------------
    // GET /ClassicTV/manage/library/series/{seriesId}/episodes
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns all episodes for the given series, ordered by season then episode number.
    /// The episode IDs are the values stored in <see cref="Show.JellyfinIds"/>.
    /// </summary>
    /// <param name="seriesId">The Jellyfin item GUID for the series.</param>
    [HttpGet("library/series/{seriesId}/episodes")]
    [ProducesResponseType(typeof(EpisodeListResponse), 200)]
    [ProducesResponseType(404)]
    public ActionResult<EpisodeListResponse> GetEpisodes([FromRoute] string seriesId)
    {
        if (!Guid.TryParse(seriesId, out var guid))
        {
            _logger.LogDebug("ClassicTV ManagementController: seriesId '{SeriesId}' is not a valid GUID", seriesId);
            return NotFound($"Series '{seriesId}' not found");
        }

        var seriesItem = _libraryManager.GetItemById(guid);
        if (seriesItem is null || seriesItem.GetBaseItemKind() != BaseItemKind.Series)
        {
            _logger.LogDebug("ClassicTV ManagementController: series '{SeriesId}' not found in library", seriesId);
            return NotFound($"Series '{seriesId}' not found");
        }

        var epQuery = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            AncestorIds = new[] { guid },
            Recursive = true,
            EnableTotalRecordCount = false
        };

        var episodes = _libraryManager.GetItemList(epQuery)
            .OfType<Episode>()
            .OrderBy(e => e.ParentIndexNumber ?? 0)
            .ThenBy(e => e.IndexNumber ?? 0)
            .Select(e => new EpisodeItem
            {
                Id = e.Id.ToString(),
                Name = e.Name ?? string.Empty,
                Season = e.ParentIndexNumber ?? 0,
                Episode = e.IndexNumber ?? 0
            })
            .ToList();

        _logger.LogDebug(
            "ClassicTV ManagementController: returning {Count} episodes for series '{SeriesId}'",
            episodes.Count,
            seriesId);

        return Ok(new EpisodeListResponse { Episodes = episodes });
    }
}
