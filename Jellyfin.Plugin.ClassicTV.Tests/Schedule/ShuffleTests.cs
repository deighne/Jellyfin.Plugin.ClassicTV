using Jellyfin.Plugin.ClassicTV.Schedule;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ClassicTV.Tests.Schedule;

/// <summary>
/// Tests for shuffle-related invariants on ScheduleEngine and semaphore/concurrency
/// invariants on ScheduleCacheWorker.
///
/// Covers the shuffle helpers directly (FisherYatesShuffle, SeedFor, ApplyShuffleToBlock)
/// plus the multiset invariant on ExpandBlockEpisodes and the semaphore behaviour of
/// ScheduleCacheWorker.TriggerRebuildAsync.
/// </summary>
public sealed class ShuffleTests
{
    // =========================================================================
    // Property 10: TriggerRebuildAsync is idempotent and always safe to call
    //              regardless of Plugin.Instance state.
    //
    // With Plugin.Instance == null, PopulateCache logs a warning and returns
    // early. Calling TriggerRebuildAsync multiple times with identical state
    // must complete without exception every time and leave the cache empty.
    // =========================================================================

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(10)]
    public async Task TriggerRebuildAsync_CalledNTimes_NeverThrows(int callCount)
    {
        var (worker, cache, _) = MakeWorker();

        for (int i = 0; i < callCount; i++)
        {
            var ex = await Record.ExceptionAsync(() => worker.TriggerRebuildAsync());
            Assert.Null(ex);
        }

        // Plugin.Instance is null in tests — no slots should be written.
        Assert.Equal(0, cache.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task TriggerRebuildAsync_WithIdenticalState_LeavesEmptyCache(int callCount)
    {
        // Same assertion as above from a different angle: the "deterministic
        // outcome" for a null plugin is zero slots, every time.
        var (worker, cache, _) = MakeWorker();

        for (int i = 0; i < callCount; i++)
            await worker.TriggerRebuildAsync();

        Assert.Equal(0, cache.Count);
    }

    // =========================================================================
    // Property 11: ExpandBlockEpisodes is a permutation of the input episode set.
    //
    // For any block whose shows have non-empty JellyfinIds, the returned slots
    // must contain exactly the same multiset of JellyfinId values as the union
    // of all shows' JellyfinIds. No duplicates, no missing IDs.
    //
    // This invariant must hold both before and after FisherYatesShuffle is
    // implemented (task 2.5). It is tested with representative inline datasets
    // including:
    //   - single show, single episode
    //   - single show, multiple episodes
    //   - multiple shows, equal weights
    //   - multiple shows, unequal weights
    //   - shows with Shuffle = true (must not lose or duplicate episodes)
    // =========================================================================

    // Inline data format: episodeCounts per show, weights per show, shuffle flags
    // Encoded as parallel arrays expressed as strings for [InlineData] compatibility.
    // Decoded in the test body.

    [Theory]
    // (episodeCounts, weights, shuffleFlags)
    [InlineData(new[] { 1 }, new[] { 1 }, new[] { false })]
    [InlineData(new[] { 3 }, new[] { 1 }, new[] { false })]
    [InlineData(new[] { 5 }, new[] { 1 }, new[] { false })]
    [InlineData(new[] { 10 }, new[] { 1 }, new[] { false })]
    [InlineData(new[] { 1 }, new[] { 1 }, new[] { true })]
    [InlineData(new[] { 3 }, new[] { 1 }, new[] { true })]
    [InlineData(new[] { 5 }, new[] { 1 }, new[] { true })]
    [InlineData(new[] { 10 }, new[] { 1 }, new[] { true })]
    [InlineData(new[] { 2, 2 }, new[] { 1, 1 }, new[] { false, false })]
    [InlineData(new[] { 2, 3 }, new[] { 1, 2 }, new[] { false, false })]
    [InlineData(new[] { 3, 3 }, new[] { 2, 1 }, new[] { false, false })]
    [InlineData(new[] { 2, 2 }, new[] { 1, 1 }, new[] { true, true })]
    [InlineData(new[] { 3, 4 }, new[] { 1, 1 }, new[] { true, false })]
    [InlineData(new[] { 1, 5 }, new[] { 2, 1 }, new[] { false, true })]
    [InlineData(new[] { 2, 3, 4 }, new[] { 1, 1, 1 }, new[] { false, false, false })]
    [InlineData(new[] { 2, 3, 4 }, new[] { 1, 2, 3 }, new[] { true, false, true })]
    [InlineData(new[] { 1, 1, 1, 1 }, new[] { 1, 1, 1, 1 }, new[] { false, false, false, false })]
    [InlineData(new[] { 3, 1, 2, 4 }, new[] { 1, 3, 2, 1 }, new[] { true, true, false, false })]
    public void ExpandBlockEpisodes_ReturnsExactEpisodeMultiset(
        int[] episodeCounts,
        int[] weights,
        bool[] shuffleFlags)
    {
        // Build shows and track expected IDs
        var shows = new List<Show>();
        var expectedIds = new List<string>();

        for (int si = 0; si < episodeCounts.Length; si++)
        {
            var ids = Enumerable.Range(0, episodeCounts[si])
                .Select(ei => $"show{si}-ep{ei}")
                .ToList();

            shows.Add(new Show
            {
                Name = $"Show{si}",
                Weight = weights[si],
                Shuffle = shuffleFlags[si],
                JellyfinIds = ids
            });

            expectedIds.AddRange(ids);
        }

        var block = new Block
        {
            Name = "Block",
            StartHour = 0,
            EndHour = 24,
            Offline = false,
            Shows = shows
        };

        var slots = ScheduleEngine.ExpandBlockEpisodes(block);

        // Must contain exactly the right count
        Assert.Equal(expectedIds.Count, slots.Count);

        // Must contain each ID exactly once (multiset equality)
        var returnedIds = slots.Select(s => s.JellyfinId).ToList();
        var expectedSorted = expectedIds.OrderBy(x => x).ToList();
        var returnedSorted = returnedIds.OrderBy(x => x).ToList();
        Assert.Equal(expectedSorted, returnedSorted);
    }

    // Large-input sweep: 10 shows × 20 episodes = 200 episodes, various weight patterns
    [Fact]
    public void ExpandBlockEpisodes_LargeInput_ReturnsAllEpisodesExactlyOnce()
    {
        const int showCount = 10;
        const int epsPerShow = 20;

        var shows = Enumerable.Range(0, showCount).Select(si => new Show
        {
            Name = $"Show{si}",
            Weight = (si % 5) + 1,   // weights 1–5, cycling
            Shuffle = si % 2 == 0,    // alternate shuffle on/off
            JellyfinIds = Enumerable.Range(0, epsPerShow)
                .Select(ei => $"s{si}e{ei}")
                .ToList()
        }).ToList();

        var block = new Block
        {
            Name = "Big Block",
            StartHour = 0,
            EndHour = 24,
            Offline = false,
            Shows = shows
        };

        var slots = ScheduleEngine.ExpandBlockEpisodes(block);

        Assert.Equal(showCount * epsPerShow, slots.Count);

        var allExpected = shows.SelectMany(s => s.JellyfinIds).OrderBy(x => x).ToList();
        var allReturned = slots.Select(s => s.JellyfinId).OrderBy(x => x).ToList();
        Assert.Equal(allExpected, allReturned);
    }

    // =========================================================================
    // Unit: disabled channel produces zero cache entries
    //
    // Plugin.Instance == null causes PopulateCache to return early, so the cache
    // stays empty regardless of what channels are in the config. This is the
    // observable proxy for the disabled-channel skip: if no plugin config is
    // available, nothing is written.
    // =========================================================================

    [Fact]
    public async Task TriggerRebuildAsync_WhenPluginInstanceNull_WritesNoCacheEntries()
    {
        var (worker, cache, _) = MakeWorker();

        // Plugin.Instance is always null in the test host — the null guard is the
        // exit path we exercise here.
        await worker.TriggerRebuildAsync();

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task TriggerRebuildAsync_WhenPluginInstanceNull_DoesNotThrow()
    {
        var (worker, _, _) = MakeWorker();

        var ex = await Record.ExceptionAsync(() => worker.TriggerRebuildAsync());

        Assert.Null(ex);
    }

    // =========================================================================
    // Unit: concurrent calls to TriggerRebuildAsync complete without exception.
    //
    // The SemaphoreSlim(1,1) ensures only one rebuild runs at a time; callers
    // queue. Firing two tasks simultaneously must not deadlock, corrupt state,
    // or throw.
    // =========================================================================

    [Fact]
    public async Task TriggerRebuildAsync_ConcurrentCalls_BothCompleteWithoutException()
    {
        var (worker, _, _) = MakeWorker();

        Exception? ex1 = null;
        Exception? ex2 = null;

        var t1 = Task.Run(async () =>
        {
            ex1 = await Record.ExceptionAsync(() => worker.TriggerRebuildAsync());
        });
        var t2 = Task.Run(async () =>
        {
            ex2 = await Record.ExceptionAsync(() => worker.TriggerRebuildAsync());
        });

        await Task.WhenAll(t1, t2);

        Assert.Null(ex1);
        Assert.Null(ex2);
    }

    [Fact]
    public async Task TriggerRebuildAsync_ManyConcurrentCalls_AllCompleteWithoutException()
    {
        var (worker, _, _) = MakeWorker();

        const int concurrency = 20;
        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(() => worker.TriggerRebuildAsync()))
            .ToArray();

        var allExceptions = await Record.ExceptionAsync(() => Task.WhenAll(tasks));

        Assert.Null(allExceptions);
    }

    [Fact]
    public async Task TriggerRebuildAsync_SemaphoreReleasedAfterCompletion_AllowsSubsequentCall()
    {
        // After concurrent rebuilds drain, the semaphore must be released so
        // a subsequent call does not deadlock. We verify by issuing a third
        // sequential call after two concurrent ones finish.
        var (worker, _, _) = MakeWorker();

        await Task.WhenAll(
            worker.TriggerRebuildAsync(),
            worker.TriggerRebuildAsync());

        // Third call must complete within a reasonable timeout.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ex = await Record.ExceptionAsync(() => worker.TriggerRebuildAsync(cts.Token));

        Assert.Null(ex);
    }

    // =========================================================================
    // Direct shuffle-helper tests
    // =========================================================================

    [Fact]
    public void FisherYatesShuffle_ReturnsPermutation_WithoutMutatingInput()
    {
        var input = Enumerable.Range(0, 50).Select(i => $"ep{i}").ToList();
        var original = new List<string>(input);

        var result = ScheduleEngine.FisherYatesShuffle(input, 12345);

        Assert.Equal(input.Count, result.Count);
        Assert.Equal(original.OrderBy(x => x), result.OrderBy(x => x));
        Assert.Equal(original, input); // input list must not be mutated
    }

    [Fact]
    public void SeedFor_IsGoldenStableAcrossProcesses()
    {
        // Golden values lock the algorithm. If this used System.HashCode or
        // string.GetHashCode() (both process-randomised in .NET), these constants
        // would not match on most runs — catching exactly the restart-instability bug
        // this seed must never have.
        Assert.Equal(-2052307441, ScheduleEngine.SeedFor("ch", "blk", "show", 7));
        Assert.Equal(-1466704600, ScheduleEngine.SeedFor("sitcoms", "All Day", "Cheers", 0));
        Assert.Equal(-376101542, ScheduleEngine.SeedFor("a", "b", "c", 1));
    }

    [Fact]
    public void SeedFor_DiffersByCycleIndex()
    {
        Assert.NotEqual(
            ScheduleEngine.SeedFor("c", "b", "s", 1),
            ScheduleEngine.SeedFor("c", "b", "s", 2));
    }

    [Fact]
    public void ApplyShuffleToBlock_IsDeterministic_AndLeavesOriginalUntouched()
    {
        var channel = new ChannelConfig
        {
            Id = "c",
            Name = "C",
            StartAnchor = "2024-01-01T00:00:00Z",
            Blocks = new()
        };
        var linearIds = Enumerable.Range(0, 20).Select(i => $"e{i}").ToList();
        var block = new Block
        {
            Name = "B",
            StartHour = 0,
            EndHour = 24,
            Shows = new List<Show>
            {
                new Show { Name = "S", Weight = 1, Shuffle = true, JellyfinIds = new List<string>(linearIds) },
                new Show { Name = "L", Weight = 1, Shuffle = false, JellyfinIds = new List<string> { "l1", "l2" } }
            }
        };

        var a = ScheduleEngine.ApplyShuffleToBlock(channel, block, 5);
        var b = ScheduleEngine.ApplyShuffleToBlock(channel, block, 5);

        // Deterministic for the same cycle index.
        Assert.Equal(a.Shows![0].JellyfinIds, b.Shows![0].JellyfinIds);

        // Original block is not mutated.
        Assert.Equal(linearIds, block.Shows[0].JellyfinIds);

        // Non-shuffle shows pass through unchanged.
        Assert.Equal(block.Shows[1].JellyfinIds, a.Shows![1].JellyfinIds);

        // A different cycle index produces a different permutation.
        var c = ScheduleEngine.ApplyShuffleToBlock(channel, block, 6);
        Assert.NotEqual(a.Shows![0].JellyfinIds, c.Shows![0].JellyfinIds);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static (ScheduleCacheWorker worker, ScheduleCache cache, DurationCache durations) MakeWorker()
    {
        var cache = new ScheduleCache();
        var durations = new DurationCache();
        var worker = new ScheduleCacheWorker(
            cache,
            durations,
            NullLogger<ScheduleCacheWorker>.Instance);
        return (worker, cache, durations);
    }
}
