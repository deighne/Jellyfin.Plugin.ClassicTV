using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ClassicTV.Schedule;

/// <summary>One show within a block's weighted rotation.</summary>
public sealed class Show
{
    /// <summary>Display name of the show.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Relative scheduling weight (1–1000).</summary>
    [JsonPropertyName("weight")]
    public int Weight { get; set; } = 1;

    /// <summary>When true, episodes play in a randomised no-repeat order seeded by cycle index.</summary>
    [JsonPropertyName("shuffle")]
    public bool Shuffle { get; set; } = false;

    /// <summary>Ordered list of Jellyfin item IDs for this show's episodes.</summary>
    [JsonPropertyName("jellyfinIds")]
    public List<string> JellyfinIds { get; set; } = new();
}

/// <summary>One time-window within a channel's broadcast day.</summary>
public sealed class Block
{
    /// <summary>Display name for this block (e.g. "Prime Time").</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Start hour in America/Chicago local time, 0–23.</summary>
    [JsonPropertyName("startHour")]
    public int StartHour { get; set; }

    /// <summary>Exclusive end hour in America/Chicago local time, 1–24.</summary>
    [JsonPropertyName("endHour")]
    public int EndHour { get; set; }

    /// <summary>When true this block plays the offline clip rather than shows.</summary>
    [JsonPropertyName("offline")]
    public bool Offline { get; set; }

    /// <summary>Shows to rotate through. Null or empty when Offline is true.</summary>
    [JsonPropertyName("shows")]
    public List<Show>? Shows { get; set; }
}

/// <summary>Complete definition of one channel.</summary>
public sealed class ChannelConfig
{
    /// <summary>Short identifier used in the API URL, e.g. "4".</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable channel name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Remote control channel number. 0 means unassigned.</summary>
    [JsonPropertyName("number")]
    public int Number { get; set; } = 0;

    /// <summary>When false the channel is hidden from the schedule API and skipped by the cache worker.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Fixed broadcast origin — ISO 8601 UTC datetime string.
    /// All schedule calculations measure elapsed time from this point.
    /// The channel is treated as having broadcast continuously since this moment.
    /// Defaults to Unix epoch if omitted, which is deterministic and safe.
    /// </summary>
    [JsonPropertyName("startAnchor")]
    public string StartAnchor { get; set; } = "1970-01-01T00:00:00Z";

    /// <summary>
    /// <see cref="StartAnchor"/> parsed to a Unix timestamp in seconds.
    /// Returns 0 (Unix epoch) if StartAnchor is missing or unparseable.
    /// </summary>
    [JsonIgnore]
    public long StartAnchorUnix
    {
        get
        {
            if (string.IsNullOrWhiteSpace(StartAnchor))
                return 0L;
            if (DateTimeOffset.TryParse(
                    StartAnchor,
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var dto))
                return dto.ToUnixTimeSeconds();
            return 0L;
        }
    }

    /// <summary>
    /// When set, this is a mirror channel: instead of a schedule it plays whatever
    /// the named Jellyfin user is currently watching, at their current position.
    /// Blocks are ignored (keep one offline block so the config stays valid).
    /// </summary>
    [JsonPropertyName("mirrorUser")]
    public string? MirrorUser { get; set; }

    /// <summary>
    /// When true, this channel is the pre-rendered TV Guide channel (15-minute video chunks).
    /// Blocks are ignored (keep one offline block so the config stays valid).
    /// </summary>
    [JsonPropertyName("guide")]
    public bool Guide { get; set; }

    /// <summary>
    /// Optional content rating ceiling ("G", "PG", "PG-13", "R", "NC-17").
    /// Null or empty means unrestricted.
    /// </summary>
    [JsonPropertyName("maxRating")]
    public string? MaxRating { get; set; }

    /// <summary>
    /// When <see cref="MaxRating"/> is set, controls whether items with no/unrated
    /// OfficialRating are allowed to play on this channel (defaults to true).
    /// </summary>
    [JsonPropertyName("allowUnrated")]
    public bool AllowUnrated { get; set; } = true;

    /// <summary>Ordered list of time-blocks making up the broadcast day.</summary>
    [JsonPropertyName("blocks")]
    public List<Block> Blocks { get; set; } = new();
}

/// <summary>Root object for the channels JSON stored in plugin configuration.</summary>
public sealed class ChannelConfigRoot
{
    [JsonPropertyName("channels")]
    public List<ChannelConfig> Channels { get; set; } = new();
}

/// <summary>One episode slot in the expanded weighted rotation.</summary>
public sealed class EpisodeSlot
{
    public string JellyfinId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string ShowName { get; init; } = string.Empty;

    /// <summary>Duration in seconds. Set to 0 by ExpandBlockEpisodes; filled by caller.</summary>
    public double Duration { get; set; }
}

/// <summary>The resolved schedule result returned by ScheduleEngine.</summary>
public sealed class ScheduleResult
{
    public string JellyfinId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    /// <summary>Seconds into the episode at which playback should begin.</summary>
    public double StartOffset { get; init; }

    /// <summary>Total episode duration in seconds.</summary>
    public double Duration { get; init; }

    public bool IsOffline { get; init; }

    public static ScheduleResult Offline => new() { IsOffline = true, Title = "Offline" };
}
