using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.ClassicTV.Schedule;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ClassicTV.Api;

/// <summary>Response body for GET /ClassicTV/channel/{channelId}.</summary>
public sealed class ChannelResponse
{
    public string ChannelId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string EpisodeTitle { get; init; } = string.Empty;
    public string JellyfinItemId { get; init; } = string.Empty;

    /// <summary>Seconds into the episode at which playback should begin.</summary>
    public double StartOffset { get; init; }

    /// <summary>Total episode duration in seconds.</summary>
    public double Duration { get; init; }

    public NextItemResponse? NextItem { get; init; }

    /// <summary>True for a mirror channel (follows a user's session, not a schedule).</summary>
    public bool Mirror { get; init; }

    /// <summary>Mirror channels only: the mirrored session is paused (position is frozen).</summary>
    public bool Paused { get; init; }

    /// <summary>Unix timestamp when this response was generated.</summary>
    public long GeneratedAt { get; init; }
}

/// <summary>Lightweight next-item payload.</summary>
public sealed class NextItemResponse
{
    public string JellyfinItemId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string EpisodeTitle { get; init; } = string.Empty;
    public double StartOffset { get; init; }
    public double Duration { get; init; }
}

/// <summary>One channel entry in the GET /ClassicTV/channels response.</summary>
public sealed class ChannelListItem
{
    /// <summary>Channel identifier used in GET /ClassicTV/channel/{id}.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable channel name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Channel number for remote digit entry and sort order. 0 = unassigned (sorted last).</summary>
    public int Number { get; init; }

    /// <summary>True for a mirror channel (plays what a user is watching).</summary>
    public bool Mirror { get; init; }
}

/// <summary>Response body for GET /ClassicTV/channels.</summary>
public sealed class ChannelListResponse
{
    public IReadOnlyList<ChannelListItem> Channels { get; init; } = Array.Empty<ChannelListItem>();
}

/// <summary>
/// REST controller for the Classic TV schedule API.
///
/// Route: GET /ClassicTV/channel/{channelId}
/// Returns the Jellyfin item ID and start offset for the current schedule slot.
/// The Kodi addon passes these directly to the Jellyfin addon's plugin:// URL.
/// </summary>
[ApiController]
[Route("ClassicTV")]
public sealed class ClassicTvController : ControllerBase
{
    private readonly ScheduleCache _cache;
    private readonly DurationCache _durationCache;
    private readonly ILogger<ClassicTvController> _logger;
    private readonly ISessionSource? _sessions;
    private readonly string _guideCacheDir;
    private readonly ILibraryManager? _libraryManager;

    public ClassicTvController(
        ScheduleCache cache,
        DurationCache durationCache,
        ILogger<ClassicTvController> logger,
        ISessionSource? sessions = null,
        string? guideCacheDir = null,
        ILibraryManager? libraryManager = null)
    {
        _cache = cache;
        _durationCache = durationCache;
        _logger = logger;
        _sessions = sessions;
        _guideCacheDir = guideCacheDir ?? GuideResolver.DefaultCacheDirectory;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// List all configured channels.
    ///
    /// Used by the Classic TV Kodi addon at startup to build its channel list
    /// for up/down navigation and direct digit entry — without hardcoding
    /// channel IDs in the addon settings.
    /// </summary>
    /// <returns>Ordered list of channel IDs and names.</returns>
    [HttpGet("channels")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ChannelListResponse), 200)]
    [ProducesResponseType(503)]
    public ActionResult<ChannelListResponse> GetChannels()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            _logger.LogError("ClassicTV: plugin configuration unavailable");
            return StatusCode(503, "Plugin configuration not available");
        }

        var root = config.ParsedChannels;
        if (root is null)
        {
            _logger.LogError("ClassicTV: channel configuration JSON is invalid");
            return StatusCode(503, "Channel configuration is invalid");
        }

        var channels = root.Channels
            .Where(c => c.Enabled)
            .OrderBy(c => c.Number == 0 ? int.MaxValue : c.Number)
            .Select(c => new ChannelListItem
            {
                Id = c.Id,
                Name = c.Name,
                Number = c.Number,
                Mirror = !string.IsNullOrWhiteSpace(c.MirrorUser)
            })
            .ToList();

        return Ok(new ChannelListResponse { Channels = channels });
    }

    /// <summary>
    /// Get the current schedule slot for a channel.
    /// </summary>
    /// <param name="channelId">Channel identifier (e.g. "4").</param>
    /// <param name="t">Optional Unix timestamp in seconds (used by the batch guide renderer).</param>
    /// <returns>Play command: item ID, start offset, duration, next item.</returns>
    [HttpGet("channel/{channelId}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ChannelResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public ActionResult<ChannelResponse> GetChannel(
        [FromRoute][Required] string channelId,
        [FromQuery] long? t = null)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            _logger.LogError("ClassicTV: plugin configuration unavailable");
            return StatusCode(503, "Plugin configuration not available");
        }

        var root = config.ParsedChannels;
        if (root is null)
        {
            _logger.LogError("ClassicTV: channel configuration JSON is invalid");
            return StatusCode(503, "Channel configuration is invalid");
        }

        var channel = root.Channels.FirstOrDefault(c =>
            string.Equals(c.Id, channelId, StringComparison.OrdinalIgnoreCase));

        if (channel is null || !channel.Enabled)
            return NotFound($"Channel '{channelId}' not found");

        long now = (t.HasValue && t.Value > 0)
            ? t.Value
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (!string.IsNullOrWhiteSpace(channel.MirrorUser))
            return GetMirrorChannel(channel, now);

        if (channel.Guide)
            return GetGuideChannel(channel, now);

        var durations = _durationCache.AsReadOnlyDictionary();
        var ratings = _durationCache.AsRatingsReadOnlyDictionary();

        // Never serve an episode that is no longer in the library (same rule the
        // cache worker applies), otherwise the client 404s on it repeatedly.
        var schedulable = ScheduleEngine.PruneMissingEpisodes(channel, durations);
        schedulable = ScheduleEngine.PruneByRating(schedulable, ratings);

        // Try cache first.
        var current = _cache.Get(channelId, now);
        if (current is null)
        {
            _logger.LogWarning("ClassicTV: cache miss for channel {ChannelId} at t={T}", channelId, now);
            current = ScheduleEngine.ComputeSchedule(schedulable, now, durations);
        }
        else if (current.Duration > 0 && current.StartOffset >= current.Duration)
        {
            // Cached slot was built with fallback durations; real duration is shorter.
            // Recompute with real durations from DurationCache.
            _logger.LogWarning(
                "ClassicTV: cached slot offset {Offset:.1f}s >= duration {Duration:.1f}s for {ItemId} — recomputing",
                current.StartOffset, current.Duration, current.JellyfinId);
            current = ScheduleEngine.ComputeSchedule(schedulable, now, durations);
        }

        if (current.IsOffline)
        {
            return Ok(new ChannelResponse
            {
                ChannelId = channelId,
                Title = "Offline",
                JellyfinItemId = string.Empty,
                StartOffset = 0,
                Duration = 0,
                GeneratedAt = now
            });
        }

        // Compute next item (current duration ahead). The slot cache is keyed by
        // minute, so sampling exactly at the episode-end boundary can round back
        // into the current episode's final minute and report the current item as
        // "next". If that happens, step one minute further to reach a real next item.
        long nextT = now + (long)Math.Max(current.Duration - current.StartOffset, 60);
        var next = _cache.Get(channelId, nextT)
                   ?? ScheduleEngine.ComputeSchedule(schedulable, nextT, durations);
        if (next.JellyfinId == current.JellyfinId)
        {
            nextT += 60;
            next = _cache.Get(channelId, nextT)
                   ?? ScheduleEngine.ComputeSchedule(schedulable, nextT, durations);
        }

        NextItemResponse? nextItem = next.IsOffline ? null : new NextItemResponse
        {
            JellyfinItemId = next.JellyfinId,
            Title = next.Title,
            EpisodeTitle = ResolveEpisodeTitle(next.JellyfinId),
            StartOffset = next.StartOffset,
            Duration = next.Duration
        };

        _logger.LogInformation(
            "ClassicTV: channel={ChannelId} item={ItemId} startOffset={Offset:.1f}s",
            channelId, current.JellyfinId, current.StartOffset);

        return Ok(new ChannelResponse
        {
            ChannelId = channelId,
            Title = current.Title,
            EpisodeTitle = ResolveEpisodeTitle(current.JellyfinId),
            JellyfinItemId = current.JellyfinId,
            StartOffset = current.StartOffset,
            Duration = current.Duration,
            NextItem = nextItem,
            GeneratedAt = now
        });
    }

    /// <summary>
    /// Mirror channel: whatever <see cref="ChannelConfig.MirrorUser"/> is watching,
    /// at their current position. Idle looks like an offline channel (empty item id).
    /// </summary>
    private ActionResult<ChannelResponse> GetMirrorChannel(ChannelConfig channel, long now)
    {
        var mirror = MirrorResolver.Resolve(
            _sessions?.GetSessions() ?? Array.Empty<MirrorSession>(),
            channel.MirrorUser!,
            DateTime.UtcNow);

        if (!mirror.Active)
        {
            return Ok(new ChannelResponse
            {
                ChannelId = channel.Id,
                Title = "Offline",
                Mirror = true,
                GeneratedAt = now
            });
        }

        _logger.LogInformation(
            "ClassicTV: mirror channel={ChannelId} user={User} item={ItemId} position={Position:.1f}s paused={Paused}",
            channel.Id, channel.MirrorUser, mirror.ItemId, mirror.StartOffset, mirror.Paused);

        return Ok(new ChannelResponse
        {
            ChannelId = channel.Id,
            Title = mirror.Title,
            EpisodeTitle = ResolveEpisodeTitle(mirror.ItemId),
            JellyfinItemId = mirror.ItemId,
            StartOffset = mirror.StartOffset,
            Duration = mirror.Duration,
            Mirror = true,
            Paused = mirror.Paused,
            GeneratedAt = now
        });
    }

    /// <summary>
    /// Stream a pre-rendered 15-minute TV Guide MP4 chunk with HTTP byte-range support.
    /// </summary>
    /// <param name="chunkId">Chunk identifier (e.g. "guide-1794182400" or "guide-1794182400.mp4").</param>
    [HttpGet("guide/stream/{chunkId}")]
    [AllowAnonymous]
    [ProducesResponseType(200)]
    [ProducesResponseType(206)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public IActionResult GetGuideStream([FromRoute][Required] string chunkId)
    {
        if (GuideResolver.NormalizeChunkId(chunkId) is null)
            return BadRequest("Invalid guide chunk ID");

        string? filePath = GuideResolver.ResolveStreamFilePath(chunkId, _guideCacheDir);
        if (filePath is null)
            return NotFound($"Guide chunk '{chunkId}' not available");

        return PhysicalFile(filePath, "video/mp4", enableRangeProcessing: true);
    }

    private string ResolveEpisodeTitle(string jellyfinItemId)
    {
        if (_libraryManager is null || !Guid.TryParse(jellyfinItemId, out var guid))
            return string.Empty;
        return _libraryManager.GetItemById(guid)?.Name ?? string.Empty;
    }

    /// <summary>
    /// Pre-rendered TV Guide channel: resolves the 15-minute MP4 chunk for <paramref name="now"/>.
    /// </summary>
    private ActionResult<ChannelResponse> GetGuideChannel(ChannelConfig channel, long now)
    {
        var resolved = GuideResolver.ResolveSlot(channel.Name, now, _guideCacheDir);
        if (!resolved.Active)
        {
            return Ok(new ChannelResponse
            {
                ChannelId = channel.Id,
                Title = "Offline",
                JellyfinItemId = string.Empty,
                StartOffset = 0,
                Duration = 0,
                GeneratedAt = now
            });
        }

        _logger.LogInformation(
            "ClassicTV: guide channel={ChannelId} chunk={ItemId} startOffset={Offset:.1f}s fallback={Fallback}",
            channel.Id, resolved.ItemId, resolved.StartOffset, resolved.IsFallback);

        return Ok(new ChannelResponse
        {
            ChannelId = channel.Id,
            Title = resolved.Title,
            JellyfinItemId = resolved.ItemId,
            StartOffset = resolved.StartOffset,
            Duration = resolved.Duration,
            NextItem = new NextItemResponse
            {
                JellyfinItemId = resolved.NextItemId,
                Title = resolved.Title,
                StartOffset = 0,
                Duration = resolved.Duration
            },
            GeneratedAt = now
        });
    }
}
