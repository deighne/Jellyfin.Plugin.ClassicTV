using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ClassicTV.Schedule;

/// <summary>Resolved playback slot for the pre-rendered TV Guide channel.</summary>
public sealed record GuideResolution(
    bool Active,
    string ItemId = "",
    string Title = "",
    double StartOffset = 0,
    double Duration = 0,
    string NextItemId = "",
    bool IsFallback = false)
{
    public static GuideResolution Offline { get; } = new(false);
}

/// <summary>
/// Resolves 15-minute pre-rendered video chunks for the TV Guide channel.
/// Decoupled from Jellyfin host types so chunk boundary math, fallback selection,
/// and path-traversal validation can be unit-tested directly against a temp directory.
/// </summary>
public static partial class GuideResolver
{
    /// <summary>Duration of each pre-rendered guide chunk in seconds (15 minutes).</summary>
    public const int ChunkDurationSeconds = 900;

    [GeneratedRegex(@"^guide-(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ChunkIdPattern();

    [GeneratedRegex(@"^guide-(\d+)\.mp4$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChunkFileNamePattern();

    /// <summary>Default directory where guide chunks are stored on the Jellyfin host.</summary>
    public static string DefaultCacheDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "Application Support",
            "jellyfin",
            "data",
            "classictv-guide");

    /// <summary>
    /// Validates a chunk identifier (with or without a trailing <c>.mp4</c> extension)
    /// and returns the canonical <c>guide-{timestamp}</c> ID, or <c>null</c> if invalid.
    /// </summary>
    public static string? NormalizeChunkId(string? rawChunkId)
    {
        if (string.IsNullOrWhiteSpace(rawChunkId))
            return null;

        string cleaned = rawChunkId.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            ? rawChunkId[..^4]
            : rawChunkId;

        return ChunkIdPattern().IsMatch(cleaned) ? cleaned : null;
    }

    /// <summary>
    /// Resolves the current schedule slot for the guide channel at <paramref name="nowUnix"/>.
    /// </summary>
    public static GuideResolution ResolveSlot(string channelName, long nowUnix, string cacheDirectory)
    {
        if (nowUnix < 0)
            nowUnix = 0;

        long chunkStart = (nowUnix / ChunkDurationSeconds) * ChunkDurationSeconds;
        long nextChunkStart = chunkStart + ChunkDurationSeconds;
        string exactId = $"guide-{chunkStart}";
        string nextId = $"guide-{nextChunkStart}";
        string title = string.IsNullOrWhiteSpace(channelName) ? "TV Guide" : channelName;

        if (string.IsNullOrWhiteSpace(cacheDirectory) || !Directory.Exists(cacheDirectory))
            return GuideResolution.Offline;

        string exactPath = Path.Combine(cacheDirectory, $"{exactId}.mp4");
        if (File.Exists(exactPath))
        {
            double offset = Math.Max(0, nowUnix - chunkStart);
            return new GuideResolution(
                Active: true,
                ItemId: exactId,
                Title: title,
                StartOffset: offset,
                Duration: ChunkDurationSeconds,
                NextItemId: nextId,
                IsFallback: false);
        }

        // Fallback: use the newest valid guide-*.mp4 file on disk so Channel 1 never
        // black-screens if the batch renderer is slightly behind.
        string? newestId = FindNewestChunkId(cacheDirectory);
        if (newestId is null)
            return GuideResolution.Offline;

        double fallbackOffset = Math.Max(0, nowUnix % ChunkDurationSeconds);
        return new GuideResolution(
            Active: true,
            ItemId: newestId,
            Title: title,
            StartOffset: fallbackOffset,
            Duration: ChunkDurationSeconds,
            NextItemId: nextId,
            IsFallback: true);
    }

    /// <summary>
    /// Resolves the physical MP4 file path for a requested <paramref name="chunkId"/>.
    /// Falls back to the newest valid chunk file in <paramref name="cacheDirectory"/> if the
    /// requested chunk is not yet on disk. Returns <c>null</c> if <paramref name="chunkId"/>
    /// is malformed or no guide chunks exist on disk.
    /// </summary>
    public static string? ResolveStreamFilePath(string? chunkId, string cacheDirectory)
    {
        string? normalized = NormalizeChunkId(chunkId);
        if (normalized is null)
            return null;

        if (string.IsNullOrWhiteSpace(cacheDirectory) || !Directory.Exists(cacheDirectory))
            return null;

        string exactPath = Path.Combine(cacheDirectory, $"{normalized}.mp4");
        if (File.Exists(exactPath))
            return exactPath;

        string? newestId = FindNewestChunkId(cacheDirectory);
        if (newestId is null)
            return null;

        string fallbackPath = Path.Combine(cacheDirectory, $"{newestId}.mp4");
        return File.Exists(fallbackPath) ? fallbackPath : null;
    }

    private static string? FindNewestChunkId(string cacheDirectory)
    {
        try
        {
            long bestTimestamp = -1;
            string? bestId = null;

            foreach (string filePath in Directory.EnumerateFiles(cacheDirectory, "guide-*.mp4"))
            {
                string fileName = Path.GetFileName(filePath);
                var match = ChunkFileNamePattern().Match(fileName);
                if (!match.Success)
                    continue;

                if (long.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out long ts))
                {
                    if (ts > bestTimestamp)
                    {
                        bestTimestamp = ts;
                        bestId = $"guide-{ts}";
                    }
                }
            }

            return bestId;
        }
        catch
        {
            return null;
        }
    }
}
