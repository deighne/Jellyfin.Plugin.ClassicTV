namespace Jellyfin.Plugin.ClassicTV.Api;

/// <summary>
/// Response body returned by <c>POST /ClassicTV/manage/channels</c> when validation
/// fails (HTTP 400). Each string in <see cref="Errors"/> describes one violation.
/// </summary>
public sealed class ValidationErrorResponse
{
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

/// <summary>A single TV series in the Jellyfin library.</summary>
public sealed class SeriesItem
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int EpisodeCount { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();
    public string? OfficialRating { get; init; }
}

/// <summary>Response body for GET /ClassicTV/manage/library/series.</summary>
public sealed class SeriesListResponse
{
    public IReadOnlyList<SeriesItem> Series { get; init; } = Array.Empty<SeriesItem>();
}

/// <summary>A single episode of a TV series.</summary>
public sealed class EpisodeItem
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int Season { get; init; }
    public int Episode { get; init; }
}

/// <summary>Response body for GET /ClassicTV/manage/library/series/{seriesId}/episodes.</summary>
public sealed class EpisodeListResponse
{
    public IReadOnlyList<EpisodeItem> Episodes { get; init; } = Array.Empty<EpisodeItem>();
}

/// <summary>A single movie in the Jellyfin library.</summary>
public sealed class MovieItem
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int? Year { get; init; }
    public double DurationSeconds { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();
    public string? OfficialRating { get; init; }
}

/// <summary>Response body for GET /ClassicTV/manage/library/movies.</summary>
public sealed class MovieListResponse
{
    public IReadOnlyList<MovieItem> Movies { get; init; } = Array.Empty<MovieItem>();
}
