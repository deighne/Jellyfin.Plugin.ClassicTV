namespace Jellyfin.Plugin.ClassicTV.Schedule;

/// <summary>
/// Normalizes MPAA and US TV Parental Guidelines ratings onto a single ordered
/// severity scale for channel content-rating restrictions.
/// </summary>
public static class ContentRating
{
    private static readonly HashSet<string> ValidMaxRatings = new(StringComparer.OrdinalIgnoreCase)
    {
        "G", "PG", "PG-13", "R", "NC-17"
    };

    /// <summary>Returns true if <paramref name="maxRating"/> is null/empty or a supported ceiling.</summary>
    public static bool IsValidMaxRating(string? maxRating)
        => string.IsNullOrWhiteSpace(maxRating) || ValidMaxRatings.Contains(maxRating.Trim());

    /// <summary>
    /// Maps an MPAA or TV rating string to an integer rank (1..5), or null if
    /// the item is unrated / unknown.
    /// </summary>
    public static int? GetRank(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating))
            return null;

        var normalized = rating.Trim().ToUpperInvariant();
        return normalized switch
        {
            "G" or "TV-G" or "TV-Y" or "TV-Y7" or "TV-Y7-FV" => 1,
            "PG" or "TV-PG" or "GP" or "PASSED" or "APPROVED" => 2,
            "PG-13" or "PG13" or "TV-14" or "M" => 3,
            "R" or "TV-MA" => 4,
            "NC-17" or "NC17" or "X" => 5,
            _ => null
        };
    }

    /// <summary>
    /// Returns true if an item with <paramref name="itemRating"/> is permitted on a
    /// channel configured with <paramref name="maxRating"/> and <paramref name="allowUnrated"/>.
    /// </summary>
    public static bool IsAllowed(string? itemRating, string? maxRating, bool allowUnrated)
    {
        if (string.IsNullOrWhiteSpace(maxRating))
            return true;

        var maxRank = GetRank(maxRating);
        if (maxRank is null)
            return true;

        var itemRank = GetRank(itemRating);
        if (itemRank is null)
            return allowUnrated;

        return itemRank.Value <= maxRank.Value;
    }
}
