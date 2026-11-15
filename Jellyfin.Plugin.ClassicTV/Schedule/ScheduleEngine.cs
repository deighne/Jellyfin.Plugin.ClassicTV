using System.Runtime.CompilerServices;

// Make internals visible to the test project.
[assembly: InternalsVisibleTo("Jellyfin.Plugin.ClassicTV.Tests")]

namespace Jellyfin.Plugin.ClassicTV.Schedule;

/// <summary>
/// Pure-function schedule engine. No I/O, no network calls, no global state.
/// All methods are deterministic: the same inputs always produce the same output.
/// </summary>
public static class ScheduleEngine
{
    // Fallback duration used when a Jellyfin ID is absent from the duration map.
    // 30 minutes matches a standard half-hour slot.
    internal const double FallbackDurationSeconds = 1800.0;

    private const int SecondsPerDay = 86400;

    private static readonly TimeZoneInfo ChicagoTz =
        TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    // ---------------------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Expand the complete weighted rotation for a block.
    ///
    /// The returned cycle contains <b>every episode of every show exactly once</b>.
    /// Ordering uses Weighted Round-Robin (WRR): a show's weight controls how
    /// closely spaced its episodes are, not how many times they air. A show with
    /// weight 2 is picked roughly twice as often as a weight-1 show until its
    /// list is exhausted, so its episodes are spread more tightly across the cycle.
    ///
    /// This is what makes the rotation advance through the whole catalog. Because
    /// the cycle always contains the same complete episode set, its total duration
    /// is stable and <c>elapsed % cycleDuration</c> is well defined across cycles.
    ///
    /// Returns an empty list when the block is offline or has no shows.
    /// Duration on each returned slot is 0 — callers must fill it in.
    /// </summary>
    public static IReadOnlyList<EpisodeSlot> ExpandBlockEpisodes(Block block)
    {
        if (block.Offline || block.Shows is null || block.Shows.Count == 0)
            return Array.Empty<EpisodeSlot>();

        // Filter out shows with no episodes — they would corrupt WRR accounting
        // and cause a divide-by-zero if shuffle pre-processing produces a zero-ID show.
        var shows = block.Shows.Where(s => s.JellyfinIds.Count > 0).ToList();
        if (shows.Count == 0)
            return Array.Empty<EpisodeSlot>();

        int n = shows.Count;

        // Total episodes across all shows — the full rotation length.
        int totalEpisodes = shows.Sum(s => s.JellyfinIds.Count);

        // Weighted interleave state. Guard against a zero weight sum (invalid
        // config) by falling back to equal weighting.
        double weightSum = shows.Sum(s => s.Weight);
        if (weightSum <= 0.0)
            weightSum = n;

        double[] deficit = new double[n];
        for (int i = 0; i < n; i++)
            deficit[i] = Math.Max(shows[i].Weight, 0) / weightSum;

        int[] cursors = new int[n];
        int[] remaining = shows.Select(s => s.JellyfinIds.Count).ToArray();
        var slots = new List<EpisodeSlot>(totalEpisodes);

        for (int _ = 0; _ < totalEpisodes; _++)
        {
            // Pick the show with the highest deficit that still has episodes.
            // Exhausted shows are skipped; lowest index wins ties.
            int best = -1;
            for (int i = 0; i < n; i++)
            {
                if (remaining[i] <= 0)
                    continue;
                if (best == -1 || deficit[i] > deficit[best])
                    best = i;
            }

            if (best == -1)
                break; // defensive: should not happen while slots < totalEpisodes

            var show = shows[best];
            slots.Add(new EpisodeSlot
            {
                JellyfinId = show.JellyfinIds[cursors[best]],
                Title = show.Name,
                ShowName = show.Name,
                Duration = 0.0
            });

            cursors[best]++;
            remaining[best]--;
            deficit[best] -= 1.0;
            for (int i = 0; i < n; i++)
                deficit[i] += Math.Max(shows[i].Weight, 0) / weightSum;
        }

        return slots;
    }

    /// <summary>
    /// Return a copy of <paramref name="channel"/> with any episode IDs that are absent
    /// from <paramref name="durations"/> removed. This is how the schedule avoids serving
    /// items that no longer exist in the Jellyfin library (e.g. after media is deleted
    /// or duplicate copies are cleaned up): a dead ID would otherwise be scheduled and
    /// the client would 404 on it repeatedly.
    ///
    /// When <paramref name="durations"/> is empty (before the first duration resolution)
    /// the channel is returned unchanged — the fallback duration is used for every item
    /// rather than pruning the whole catalog.
    ///
    /// Pure: the input is never mutated. Returns the original instance when nothing is
    /// pruned.
    /// </summary>
    public static ChannelConfig PruneMissingEpisodes(
        ChannelConfig channel,
        IReadOnlyDictionary<string, double> durations)
    {
        if (durations.Count == 0 || channel.Blocks is null || channel.Blocks.Count == 0)
            return channel;

        var newBlocks = new List<Block>(channel.Blocks.Count);
        bool changed = false;

        foreach (var block in channel.Blocks)
        {
            if (block.Offline || block.Shows is null || block.Shows.Count == 0)
            {
                newBlocks.Add(block);
                continue;
            }

            var newShows = new List<Show>(block.Shows.Count);
            foreach (var show in block.Shows)
            {
                var kept = show.JellyfinIds.Where(durations.ContainsKey).ToList();
                if (kept.Count != show.JellyfinIds.Count)
                    changed = true;
                newShows.Add(new Show
                {
                    Name = show.Name,
                    Weight = show.Weight,
                    Shuffle = show.Shuffle,
                    JellyfinIds = kept
                });
            }

            newBlocks.Add(new Block
            {
                Name = block.Name,
                StartHour = block.StartHour,
                EndHour = block.EndHour,
                Offline = block.Offline,
                Shows = newShows
            });
        }

        if (!changed)
            return channel;

        return new ChannelConfig
        {
            Id = channel.Id,
            Name = channel.Name,
            Number = channel.Number,
            Enabled = channel.Enabled,
            StartAnchor = channel.StartAnchor,
            MirrorUser = channel.MirrorUser,
            Guide = channel.Guide,
            MaxRating = channel.MaxRating,
            AllowUnrated = channel.AllowUnrated,
            Blocks = newBlocks
        };
    }

    /// <summary>
    /// Filters out episodes/movies whose resolved rating exceeds <see cref="ChannelConfig.MaxRating"/>
    /// (or are unrated when <see cref="ChannelConfig.AllowUnrated"/> is false).
    /// Pure: returns the original instance when unrestricted or nothing is pruned.
    /// </summary>
    public static ChannelConfig PruneByRating(
        ChannelConfig channel,
        IReadOnlyDictionary<string, string?> ratings)
    {
        if (string.IsNullOrWhiteSpace(channel.MaxRating)
            || ratings.Count == 0
            || channel.Blocks is null
            || channel.Blocks.Count == 0)
        {
            return channel;
        }

        var newBlocks = new List<Block>(channel.Blocks.Count);
        bool changed = false;

        foreach (var block in channel.Blocks)
        {
            if (block.Offline || block.Shows is null || block.Shows.Count == 0)
            {
                newBlocks.Add(block);
                continue;
            }

            var newShows = new List<Show>(block.Shows.Count);
            foreach (var show in block.Shows)
            {
                var kept = show.JellyfinIds.Where(id =>
                {
                    ratings.TryGetValue(id, out var itemRating);
                    return ContentRating.IsAllowed(itemRating, channel.MaxRating, channel.AllowUnrated);
                }).ToList();

                if (kept.Count != show.JellyfinIds.Count)
                    changed = true;

                newShows.Add(new Show
                {
                    Name = show.Name,
                    Weight = show.Weight,
                    Shuffle = show.Shuffle,
                    JellyfinIds = kept
                });
            }

            newBlocks.Add(new Block
            {
                Name = block.Name,
                StartHour = block.StartHour,
                EndHour = block.EndHour,
                Offline = block.Offline,
                Shows = newShows
            });
        }

        if (!changed)
            return channel;

        return new ChannelConfig
        {
            Id = channel.Id,
            Name = channel.Name,
            Number = channel.Number,
            Enabled = channel.Enabled,
            StartAnchor = channel.StartAnchor,
            MirrorUser = channel.MirrorUser,
            Guide = channel.Guide,
            MaxRating = channel.MaxRating,
            AllowUnrated = channel.AllowUnrated,
            Blocks = newBlocks
        };
    }

    /// <summary>
    /// Compute the current schedule item for the given channel at Unix timestamp
    /// <paramref name="unixSeconds"/>.
    ///
    /// Elapsed time is measured from <see cref="ChannelConfig.StartAnchorUnix"/>,
    /// treating the channel as having broadcast continuously since that fixed origin.
    /// For multi-block channels only time spent inside the active block's window
    /// counts toward that block's elapsed rotation time.
    ///
    /// <paramref name="episodeDurations"/> maps Jellyfin ID → actual duration in seconds.
    /// Missing IDs fall back to <see cref="FallbackDurationSeconds"/>.
    /// </summary>
    public static ScheduleResult ComputeSchedule(
        ChannelConfig channel,
        long unixSeconds,
        IReadOnlyDictionary<string, double>? episodeDurations = null)
    {
        // Step 1: resolve local Chicago time
        var utc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, ChicagoTz);
        int localHour = local.Hour;

        // Step 2: find the active block
        var block = FindActiveBlock(channel, localHour);

        // Step 3: no block or explicit offline
        if (block is null || block.Offline)
            return ScheduleResult.Offline;

        // Step 4: compute block-aware elapsed time since the channel's start anchor.
        // For a 24/7 block this is simply (now - anchor).
        // For a partial-day block only time spent inside the block window counts.
        double elapsed = BlockAwareElapsed(block, channel.StartAnchorUnix, unixSeconds, local);

        if (block.Shows is null || block.Shows.Count == 0)
            return ScheduleResult.Offline;

        var durMap = episodeDurations ?? new Dictionary<string, double>();

        // Step 5: compute cycle duration directly from the catalog. The full-catalog
        // rotation contains every episode exactly once, so the sum is order-independent —
        // there is no need to expand the block just to measure it.
        double cycleDuration = 0.0;
        foreach (var show in block.Shows)
            foreach (var id in show.JellyfinIds)
                cycleDuration += durMap.TryGetValue(id, out var d0) ? d0 : FallbackDurationSeconds;

        if (cycleDuration <= 0.0)
            return ScheduleResult.Offline;

        // Step 6: derive the rotation cycle index and apply per-show shuffle.
        // Seeded by (channelId, blockName, showName, cycleIndex) so every show gets a
        // stable, reproducible permutation for the entire cycle, changing only when the
        // rotation advances to the next cycle. Shuffle reorders episodes but does not
        // change the cycle's total duration, so cycleDuration stays valid.
        long cycleIndex = (long)(elapsed / cycleDuration);
        var shuffledBlock = ApplyShuffleToBlock(channel, block, cycleIndex);

        // Step 7: expand with the (possibly shuffled) episode order and attach durations.
        var shuffledRaw = ExpandBlockEpisodes(shuffledBlock);
        if (shuffledRaw.Count == 0)
            return ScheduleResult.Offline;

        var slots = shuffledRaw.Select(s => new EpisodeSlot
        {
            JellyfinId = s.JellyfinId,
            Title = s.Title,
            ShowName = s.ShowName,
            Duration = durMap.TryGetValue(s.JellyfinId, out var d) ? d : FallbackDurationSeconds
        }).ToList();

        // Step 7: position within the current rotation cycle
        double position = elapsed % cycleDuration;

        // Step 8: walk slots to find the current item
        foreach (var slot in slots)
        {
            position -= slot.Duration;
            if (position < 0)
            {
                double seekOffset = slot.Duration + position; // position is negative here
                // Clamp: 0 <= seekOffset < duration
                seekOffset = Math.Max(0.0, Math.Min(seekOffset, slot.Duration - 1e-9));
                return new ScheduleResult
                {
                    JellyfinId = slot.JellyfinId,
                    Title = slot.Title,
                    StartOffset = seekOffset,
                    Duration = slot.Duration
                };
            }
        }

        // Floating-point drift edge case: return last slot at offset 0.
        var last = slots[^1];
        return new ScheduleResult
        {
            JellyfinId = last.JellyfinId,
            Title = last.Title,
            StartOffset = 0.0,
            Duration = last.Duration
        };
    }

    // ---------------------------------------------------------------------------
    // Internal helpers (internal for test access)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Returns a shallow copy of <paramref name="block"/> where each show with
    /// <c>Shuffle = true</c> has its <c>JellyfinIds</c> permuted using Fisher-Yates
    /// seeded by <c>(channel.Id, block.Name, show.Name, cycleIndex)</c>.
    ///
    /// Shows with <c>Shuffle = false</c> are passed through unchanged (same list reference).
    /// The returned <see cref="Block"/> is a new object; the original is not mutated.
    /// </summary>
    internal static Block ApplyShuffleToBlock(ChannelConfig channel, Block block, long cycleIndex)
    {
        if (block.Shows is null || block.Shows.Count == 0)
            return block;

        bool anyShuffled = block.Shows.Any(s => s.Shuffle && s.JellyfinIds.Count > 0);
        if (!anyShuffled)
            return block; // fast path — no copy needed

        var newShows = new List<Show>(block.Shows.Count);
        foreach (var show in block.Shows)
        {
            if (show.Shuffle && show.JellyfinIds.Count > 0)
            {
                int seed = SeedFor(channel.Id, block.Name, show.Name, cycleIndex);
                var shuffled = FisherYatesShuffle(show.JellyfinIds, seed);
                newShows.Add(new Show
                {
                    Name = show.Name,
                    Weight = show.Weight,
                    Shuffle = show.Shuffle,
                    JellyfinIds = shuffled
                });
            }
            else
            {
                newShows.Add(show);
            }
        }

        return new Block
        {
            Name = block.Name,
            StartHour = block.StartHour,
            EndHour = block.EndHour,
            Offline = block.Offline,
            Shows = newShows
        };
    }

    /// <summary>
    /// Deterministic Fisher-Yates shuffle. Returns a new list containing the same
    /// elements as <paramref name="items"/> in a permuted order.
    /// Uses <c>new Random(seed)</c> intentionally — determinism requires a seeded PRNG,
    /// not a cryptographically unpredictable source.
    /// </summary>
    internal static List<string> FisherYatesShuffle(List<string> items, int seed)
    {
        if (items.Count == 0)
            return new List<string>();

        var result = new List<string>(items);
        var rng = new Random(seed);
        for (int i = result.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (result[i], result[j]) = (result[j], result[i]);
        }
        return result;
    }

    /// <summary>
    /// Derives a deterministic seed for a given (channelId, blockName, showName, cycleIndex)
    /// tuple. The same tuple always produces the same seed, across processes and restarts.
    ///
    /// Uses FNV-1a rather than <see cref="HashCode"/> or <c>string.GetHashCode()</c>: both of
    /// those are randomised per process in .NET, which would change the shuffle order on every
    /// Jellyfin restart. The entire reason this seed exists is to be stable across restarts.
    /// </summary>
    internal static int SeedFor(string channelId, string blockName, string showName, long cycleIndex)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        uint hash = offsetBasis;
        AddPart(ref hash, channelId);
        AddPart(ref hash, blockName);
        AddPart(ref hash, showName);
        AddPart(ref hash, cycleIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return (int)hash;

        static void AddPart(ref uint hash, string? part)
        {
            foreach (char c in part ?? string.Empty)
            {
                hash ^= (byte)(c & 0xFF);
                hash *= prime;
                hash ^= (byte)(c >> 8);
                hash *= prime;
            }

            hash ^= 0xFF; // part separator
            hash *= prime;
        }
    }

    internal static Block? FindActiveBlock(ChannelConfig channel, int localHour)
    {
        foreach (var block in channel.Blocks)
            if (BlockIsActive(block, localHour))
                return block;
        return null;
    }

    internal static bool BlockIsActive(Block block, int localHour)
    {
        int s = block.StartHour;
        int e = block.EndHour;

        if (e > s)
            return localHour >= s && localHour < e;
        else
            return localHour >= s || localHour < e; // midnight-crossing
    }

    /// <summary>
    /// Compute the total number of seconds spent inside <paramref name="block"/>'s
    /// daily time window since <paramref name="anchorUnix"/>, up to and including
    /// the current position within today's window at <paramref name="unixSeconds"/>.
    ///
    /// For a 24/7 block (startHour=0, endHour=24) this simplifies to
    /// <c>unixSeconds - anchorUnix</c>.
    ///
    /// For a partial-day block (e.g. 15–17) only the 2h/day windows count.
    /// This ensures the rotation advances at the block's own rate regardless of
    /// how many hours per day the block is active.
    /// </summary>
    internal static double BlockAwareElapsed(
        Block block,
        long anchorUnix,
        long unixSeconds,
        DateTime localNow)
    {
        int blockDurationPerDaySeconds = BlockDurationPerDay(block);

        // 24/7 block — fast path
        if (blockDurationPerDaySeconds == SecondsPerDay)
            return Math.Max(0.0, unixSeconds - anchorUnix);

        // Partial-day block: count completed whole days since anchor, then add
        // seconds elapsed so far in today's window.
        double totalWallSeconds = Math.Max(0.0, unixSeconds - anchorUnix);
        long completedDays = (long)(totalWallSeconds / SecondsPerDay);

        // Seconds elapsed inside today's block window
        double todayBlockSeconds = SecondsIntoBlockWindow(block, localNow);

        return (completedDays * blockDurationPerDaySeconds) + todayBlockSeconds;
    }

    /// <summary>
    /// Returns the number of seconds the block is active per calendar day.
    /// For a normal block (endHour > startHour): (endHour - startHour) * 3600.
    /// For a midnight-crossing block: (24 - startHour + endHour) * 3600.
    /// </summary>
    internal static int BlockDurationPerDay(Block block)
    {
        if (block.EndHour > block.StartHour)
            return (block.EndHour - block.StartHour) * 3600;
        else
            return (24 - block.StartHour + block.EndHour) * 3600;
    }

    /// <summary>
    /// Returns how many seconds we are into the block's current window at
    /// <paramref name="localNow"/> (already in America/Chicago).
    ///
    /// For a normal block (e.g. 15–17): seconds since startHour today.
    /// For a midnight-crossing block (e.g. 22–06): seconds since startHour,
    /// accounting for the window that started yesterday if we're before midnight.
    /// </summary>
    internal static double SecondsIntoBlockWindow(Block block, DateTime localNow)
    {
        int localHour = localNow.Hour;
        int localMinute = localNow.Minute;
        int localSecond = localNow.Second;
        int secondsIntoDay = localHour * 3600 + localMinute * 60 + localSecond;

        if (block.EndHour > block.StartHour)
        {
            // Normal block: window started at startHour today
            int windowStart = block.StartHour * 3600;
            return Math.Max(0.0, secondsIntoDay - windowStart);
        }
        else
        {
            // Midnight-crossing block
            int windowStart = block.StartHour * 3600;
            if (localHour >= block.StartHour)
            {
                // Before midnight in the window
                return secondsIntoDay - windowStart;
            }
            else
            {
                // After midnight in the window: add the pre-midnight portion
                int preMidnightSeconds = SecondsPerDay - windowStart;
                return preMidnightSeconds + secondsIntoDay;
            }
        }
    }
}
