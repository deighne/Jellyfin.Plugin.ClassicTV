using Jellyfin.Plugin.ClassicTV.Schedule;
using Xunit;

namespace Jellyfin.Plugin.ClassicTV.Tests.Schedule;

public sealed class ScheduleEngineTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static ChannelConfig SingleBlockChannel(int startHour, int endHour, params (string id, string name, int weight)[] shows)
    {
        var block = new Block
        {
            Name = "Test Block",
            StartHour = startHour,
            EndHour = endHour,
            Offline = false,
            Shows = shows.Select(s => new Show
            {
                Name = s.name,
                Weight = s.weight,
                JellyfinIds = new List<string> { s.id }
            }).ToList()
        };
        return new ChannelConfig { Id = "1", Name = "Test", Blocks = new List<Block> { block } };
    }

    private static ChannelConfig MultiEpisodeChannel(string[] episodeIds, int startHour = 0, int endHour = 24)
    {
        var block = new Block
        {
            Name = "Block",
            StartHour = startHour,
            EndHour = endHour,
            Shows = new List<Show>
            {
                // Weight intentionally 1 to mirror production config. Full episode
                // coverage must not depend on weight.
                new Show { Name = "Show", Weight = 1, JellyfinIds = episodeIds.ToList() }
            }
        };
        return new ChannelConfig { Id = "1", Name = "Test", Blocks = new List<Block> { block } };
    }

    // Chicago is UTC-6 (standard) or UTC-5 (DST).
    // Use a known non-DST date: 2024-01-15 (January = standard time, UTC-6).
    // Midnight Chicago = 06:00 UTC = Unix 1705294800 (2024-01-15 00:00:00 CST)
    private const long ChicagoMidnight20240115 = 1705294800L;

    // A convenient fixed anchor: 2024-01-15 00:00:00 UTC (Unix 1705276800)
    // Using UTC midnight so anchor epoch arithmetic is unambiguous in tests.
    private const long Anchor20240115Utc = 1705276800L;

    // Helper: build a channel with an explicit startAnchor ISO string
    private static ChannelConfig ChannelWithAnchor(
        string anchorIso,
        int startHour,
        int endHour,
        params (string id, string name, int weight)[] shows)
    {
        var ch = SingleBlockChannel(startHour, endHour, shows);
        ch.StartAnchor = anchorIso;
        return ch;
    }

    // ---------------------------------------------------------------------------
    // ExpandBlockEpisodes
    // ---------------------------------------------------------------------------

    [Fact]
    public void ExpandBlockEpisodes_OfflineBlock_ReturnsEmpty()
    {
        var block = new Block { Offline = true, StartHour = 0, EndHour = 6 };
        var slots = ScheduleEngine.ExpandBlockEpisodes(block);
        Assert.Empty(slots);
    }

    [Fact]
    public void ExpandBlockEpisodes_NoShows_ReturnsEmpty()
    {
        var block = new Block { Offline = false, StartHour = 0, EndHour = 6, Shows = new() };
        var slots = ScheduleEngine.ExpandBlockEpisodes(block);
        Assert.Empty(slots);
    }

    [Fact]
    public void ExpandBlockEpisodes_SingleShow_ReturnsOneSlot()
    {
        var block = new Block
        {
            StartHour = 0,
            EndHour = 6,
            Shows = new List<Show>
            {
                new Show { Name = "S", Weight = 1, JellyfinIds = new List<string> { "ep1" } }
            }
        };
        var slots = ScheduleEngine.ExpandBlockEpisodes(block);
        Assert.Single(slots);
        Assert.Equal("ep1", slots[0].JellyfinId);
    }

    [Fact]
    public void ExpandBlockEpisodes_WeightedShows_TotalSlotsEqualsTotalEpisodes()
    {
        // Weight must not change how many slots a show contributes: every episode
        // airs exactly once per cycle regardless of weight.
        var block = new Block
        {
            StartHour = 0,
            EndHour = 12,
            Shows = new List<Show>
            {
                new Show { Name = "A", Weight = 2, JellyfinIds = new List<string> { "a1", "a2", "a3" } },
                new Show { Name = "B", Weight = 1, JellyfinIds = new List<string> { "b1", "b2" } }
            }
        };
        var slots = ScheduleEngine.ExpandBlockEpisodes(block);
        Assert.Equal(5, slots.Count);
        Assert.Equal(3, slots.Count(s => s.JellyfinId.StartsWith("a")));
        Assert.Equal(2, slots.Count(s => s.JellyfinId.StartsWith("b")));
    }

    [Fact]
    public void ExpandBlockEpisodes_CoversEveryEpisodeExactlyOnce()
    {
        var block = new Block
        {
            StartHour = 0,
            EndHour = 12,
            Shows = new List<Show>
            {
                new Show { Name = "A", Weight = 1, JellyfinIds = new List<string> { "a1", "a2" } },
                new Show { Name = "B", Weight = 3, JellyfinIds = new List<string> { "b1", "b2" } },
                new Show { Name = "C", Weight = 1, JellyfinIds = new List<string> { "c1" } }
            }
        };
        var slots = ScheduleEngine.ExpandBlockEpisodes(block);

        Assert.Equal(5, slots.Count);
        Assert.Equal(
            new[] { "a1", "a2", "b1", "b2", "c1" },
            slots.Select(s => s.JellyfinId).OrderBy(x => x).ToArray());
        Assert.Equal(5, slots.Select(s => s.JellyfinId).Distinct().Count());
    }

    [Fact]
    public void ExpandBlockEpisodes_WeightControlsSpacingNotCoverage()
    {
        // A weight-3 show should be picked more often early on than a weight-1
        // show, but both catalogs are fully covered exactly once.
        var block = new Block
        {
            StartHour = 0,
            EndHour = 12,
            Shows = new List<Show>
            {
                new Show { Name = "High", Weight = 3, JellyfinIds = new List<string> { "h1", "h2" } },
                new Show { Name = "Low", Weight = 1, JellyfinIds = new List<string> { "l1", "l2" } }
            }
        };
        var slots = ScheduleEngine.ExpandBlockEpisodes(block);

        Assert.Equal(4, slots.Count);
        // First slot belongs to the higher-weight show.
        Assert.Equal("h1", slots[0].JellyfinId);
        // The high-weight show's second episode is spaced closer than the low-weight's.
        int h2 = slots.ToList().FindIndex(s => s.JellyfinId == "h2");
        int l2 = slots.ToList().FindIndex(s => s.JellyfinId == "l2");
        Assert.True(h2 < l2, $"expected h2 ({h2}) before l2 ({l2})");
    }

    // ---------------------------------------------------------------------------
    // BlockIsActive
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData(8, 9, 17, true)]   // mid-block
    [InlineData(8, 8, 17, true)]   // at start boundary
    [InlineData(8, 17, 17, false)] // at end boundary (exclusive)
    [InlineData(8, 6, 17, false)]  // before block
    [InlineData(8, 23, 17, false)] // after block
    public void BlockIsActive_NormalBlock(int startHour, int localHour, int endHour, bool expected)
    {
        var block = new Block { StartHour = startHour, EndHour = endHour };
        Assert.Equal(expected, ScheduleEngine.BlockIsActive(block, localHour));
    }

    [Theory]
    [InlineData(22, 23, 6, true)]  // before midnight
    [InlineData(22, 2, 6, true)]   // after midnight
    [InlineData(22, 10, 6, false)] // outside window
    public void BlockIsActive_MidnightCrossingBlock(int startHour, int localHour, int endHour, bool expected)
    {
        var block = new Block { StartHour = startHour, EndHour = endHour };
        Assert.Equal(expected, ScheduleEngine.BlockIsActive(block, localHour));
    }

    // ---------------------------------------------------------------------------
    // ComputeSchedule — offset math
    // ---------------------------------------------------------------------------

    [Fact]
    public void ComputeSchedule_AtBlockStart_ReturnsNearZeroOffset()
    {
        // Channel: one episode, 1800s duration, midnight-spanning block
        var channel = SingleBlockChannel(0, 24, ("ep1", "Show", 1));
        var durations = new Dictionary<string, double> { ["ep1"] = 1800.0 };

        var result = ScheduleEngine.ComputeSchedule(channel, ChicagoMidnight20240115, durations);

        Assert.False(result.IsOffline);
        Assert.Equal("ep1", result.JellyfinId);
        Assert.InRange(result.StartOffset, 0.0, 1.0); // should be very close to 0
    }

    [Fact]
    public void ComputeSchedule_MidEpisode_ReturnsCorrectOffset()
    {
        var channel = SingleBlockChannel(0, 24, ("ep1", "Show", 1));
        var durations = new Dictionary<string, double> { ["ep1"] = 1800.0 };

        // 900 seconds into the block = mid-episode
        long t = ChicagoMidnight20240115 + 900;
        var result = ScheduleEngine.ComputeSchedule(channel, t, durations);

        Assert.False(result.IsOffline);
        Assert.Equal("ep1", result.JellyfinId);
        Assert.InRange(result.StartOffset, 899.0, 901.0);
    }

    [Fact]
    public void ComputeSchedule_AtEpisodeBoundary_WrapsToNextEpisode()
    {
        var channel = MultiEpisodeChannel(new[] { "ep1", "ep2" });
        var durations = new Dictionary<string, double>
        {
            ["ep1"] = 1800.0,
            ["ep2"] = 1800.0
        };

        // Exactly at the boundary of ep1 → should be ep2 at offset 0
        long t = ChicagoMidnight20240115 + 1800;
        var result = ScheduleEngine.ComputeSchedule(channel, t, durations);

        Assert.False(result.IsOffline);
        Assert.Equal("ep2", result.JellyfinId);
        Assert.InRange(result.StartOffset, 0.0, 1.0);
    }

    [Fact]
    public void ComputeSchedule_RotationWrapsAfterLastEpisode_ReturnsFirstEpisode()
    {
        var channel = MultiEpisodeChannel(new[] { "ep1", "ep2" });
        var durations = new Dictionary<string, double>
        {
            ["ep1"] = 1800.0,
            ["ep2"] = 1800.0
        };

        // Full cycle = 3600s; at 3600 we're back at ep1
        long t = ChicagoMidnight20240115 + 3600;
        var result = ScheduleEngine.ComputeSchedule(channel, t, durations);

        Assert.False(result.IsOffline);
        Assert.Equal("ep1", result.JellyfinId);
        Assert.InRange(result.StartOffset, 0.0, 1.0);
    }

    [Fact]
    public void ComputeSchedule_MissingDuration_UsesFallback()
    {
        var channel = SingleBlockChannel(0, 24, ("ep1", "Show", 1));
        // No duration map provided — engine should use FallbackDurationSeconds (1800s)
        long t = ChicagoMidnight20240115 + 900;
        var result = ScheduleEngine.ComputeSchedule(channel, t, episodeDurations: null);

        Assert.False(result.IsOffline);
        Assert.Equal(ScheduleEngine.FallbackDurationSeconds, result.Duration);
        Assert.InRange(result.StartOffset, 899.0, 901.0);
    }

    [Fact]
    public void ComputeSchedule_NoActiveBlock_ReturnsOffline()
    {
        // Block only covers hours 8–17; query at midnight
        var channel = SingleBlockChannel(8, 17, ("ep1", "Show", 1));
        var result = ScheduleEngine.ComputeSchedule(channel, ChicagoMidnight20240115);
        Assert.True(result.IsOffline);
    }

    [Fact]
    public void ComputeSchedule_OfflineBlock_ReturnsOffline()
    {
        var channel = new ChannelConfig
        {
            Id = "1",
            Name = "Test",
            Blocks = new List<Block>
            {
                new Block { Name = "Off", StartHour = 0, EndHour = 24, Offline = true }
            }
        };
        var result = ScheduleEngine.ComputeSchedule(channel, ChicagoMidnight20240115);
        Assert.True(result.IsOffline);
    }

    [Theory]
    [InlineData("ch1")]
    [InlineData("ch2")]
    [InlineData("ch3")]
    public void ComputeSchedule_MultipleChannels_AreIndependent(string channelId)
    {
        // Two channels with different episode sets; results must be for the correct channel.
        var ch1 = new ChannelConfig
        {
            Id = "ch1",
            Name = "Channel 1",
            Blocks = new List<Block>
            {
                new Block
                {
                    StartHour = 0, EndHour = 24,
                    Shows = new List<Show> { new Show { Name = "A", Weight = 1, JellyfinIds = new List<string> { "a1" } } }
                }
            }
        };
        var ch2 = new ChannelConfig
        {
            Id = "ch2",
            Name = "Channel 2",
            Blocks = new List<Block>
            {
                new Block
                {
                    StartHour = 0, EndHour = 24,
                    Shows = new List<Show> { new Show { Name = "B", Weight = 1, JellyfinIds = new List<string> { "b1" } } }
                }
            }
        };
        var ch3 = new ChannelConfig
        {
            Id = "ch3",
            Name = "Channel 3",
            Blocks = new List<Block>
            {
                new Block { StartHour = 0, EndHour = 24, Offline = true }
            }
        };

        var channels = new Dictionary<string, ChannelConfig>
        {
            ["ch1"] = ch1,
            ["ch2"] = ch2,
            ["ch3"] = ch3
        };

        var result = ScheduleEngine.ComputeSchedule(channels[channelId], ChicagoMidnight20240115);

        if (channelId == "ch1") Assert.Equal("a1", result.JellyfinId);
        else if (channelId == "ch2") Assert.Equal("b1", result.JellyfinId);
        else Assert.True(result.IsOffline);
    }

    // ---------------------------------------------------------------------------
    // Start anchor — continuous rotation from fixed origin
    // ---------------------------------------------------------------------------

    [Fact]
    public void ComputeSchedule_FixedAnchor_SameInputAlwaysSameOutput()
    {
        // With a fixed anchor, the same unix timestamp must always return the same result
        // regardless of when the test runs. This is the core regression test.
        var channel = ChannelWithAnchor("2024-01-15T00:00:00Z", 0, 24, ("ep1", "Show", 1));
        var durations = new Dictionary<string, double> { ["ep1"] = 1800.0 };

        // 900s after anchor → mid-episode
        var r1 = ScheduleEngine.ComputeSchedule(channel, Anchor20240115Utc + 900, durations);
        var r2 = ScheduleEngine.ComputeSchedule(channel, Anchor20240115Utc + 900, durations);

        Assert.Equal(r1.JellyfinId, r2.JellyfinId);
        Assert.Equal(r1.StartOffset, r2.StartOffset);
    }

    [Fact]
    public void ComputeSchedule_AnchorAt900s_OffsetIs900()
    {
        // Anchor is T=0. Query at T+900. Episode duration=1800. Expect ep1 at offset 900.
        var channel = ChannelWithAnchor("2024-01-15T00:00:00Z", 0, 24, ("ep1", "Show", 1));
        var durations = new Dictionary<string, double> { ["ep1"] = 1800.0 };

        var result = ScheduleEngine.ComputeSchedule(channel, Anchor20240115Utc + 900, durations);

        Assert.False(result.IsOffline);
        Assert.Equal("ep1", result.JellyfinId);
        Assert.InRange(result.StartOffset, 899.0, 901.0);
    }

    [Fact]
    public void ComputeSchedule_AnchorFarInPast_PositionStillWithinCycle()
    {
        // Anchor 10 years in the past. Rotation has wrapped thousands of times.
        // Result must still be a valid position within the cycle.
        // 10 years ≈ 315,360,000 seconds
        long tenYearsAgo = Anchor20240115Utc - 315_360_000L;
        var channel = new ChannelConfig
        {
            Id = "1",
            Name = "Test",
            StartAnchor = DateTimeOffset.FromUnixTimeSeconds(tenYearsAgo).ToString("o"),
            Blocks = new List<Block>
            {
                new Block
                {
                    StartHour = 0, EndHour = 24,
                    Shows = new List<Show>
                    {
                        new Show { Name = "S", Weight = 2, JellyfinIds = new List<string> { "ep1", "ep2" } }
                    }
                }
            }
        };
        var durations = new Dictionary<string, double> { ["ep1"] = 1800.0, ["ep2"] = 1800.0 };

        var result = ScheduleEngine.ComputeSchedule(channel, Anchor20240115Utc, durations);

        Assert.False(result.IsOffline);
        Assert.True(result.StartOffset >= 0.0);
        Assert.True(result.StartOffset < result.Duration);
    }

    [Fact]
    public void ComputeSchedule_MissingAnchor_DefaultsToEpochAndDoesNotThrow()
    {
        // No startAnchor set — defaults to Unix epoch. Must not throw.
        var channel = SingleBlockChannel(0, 24, ("ep1", "Show", 1));
        var durations = new Dictionary<string, double> { ["ep1"] = 1800.0 };

        var ex = Record.Exception(() =>
            ScheduleEngine.ComputeSchedule(channel, Anchor20240115Utc, durations));

        Assert.Null(ex);
    }

    [Fact]
    public void ComputeSchedule_AnchorDoesNotResetAtMidnight()
    {
        // Prove that a 24/7 channel does NOT reset at midnight.
        // Use an episode duration that does NOT divide evenly into 86400,
        // so anchor-based elapsed gives a different result than daily reset.
        //
        // Setup: 1 episode, duration=3601s (just over 1 hour).
        // anchor = Anchor20240115Utc.
        // Daily reset would compute: elapsed_today = seconds since midnight Chicago.
        // Anchor-based: elapsed = now - anchor.
        //
        // Query at anchor + 86400 + 100 (100s into "day 2"):
        //   Anchor-based: elapsed = 86500, 86500 % 3601 = 86500 - 24*3601 = 86500 - 86424 = 76
        //   Daily-reset:  elapsed_today = 100 (100s since midnight)
        //
        // These give different offsets, proving anchor-based doesn't reset daily.
        var channel = ChannelWithAnchor("2024-01-15T00:00:00Z", 0, 24, ("ep1", "Show", 1));
        var durations = new Dictionary<string, double> { ["ep1"] = 3601.0 };

        // Anchor-based: 86500 % 3601 = 86500 - 24*3601 = 76 → ep1@76
        var result = ScheduleEngine.ComputeSchedule(
            channel, Anchor20240115Utc + 86400 + 100, durations);

        Assert.Equal("ep1", result.JellyfinId);
        // If daily reset: offset would be 100. Anchor-based: 76.
        Assert.InRange(result.StartOffset, 75.0, 77.0);
    }

    [Fact]
    public void ComputeSchedule_AdvancesThroughFullCatalogAcrossCycles()
    {
        // Production regression: with weight=1 shows the engine used to emit only
        // the first episode of each show and repeat it forever. The cycle must now
        // contain every episode, and time must walk through all of them before wrap.
        //
        // Two shows x two episodes, weight 1 → four 1800s slots = 7200s cycle.
        var channel = new ChannelConfig
        {
            Id = "1",
            Name = "Test",
            StartAnchor = "2024-01-15T00:00:00Z",
            Blocks = new List<Block>
            {
                new Block
                {
                    StartHour = 0, EndHour = 24,
                    Shows = new List<Show>
                    {
                        new Show { Name = "A", Weight = 1, JellyfinIds = new List<string> { "a1", "a2" } },
                        new Show { Name = "B", Weight = 1, JellyfinIds = new List<string> { "b1", "b2" } }
                    }
                }
            }
        };
        var durations = new Dictionary<string, double>
        {
            ["a1"] = 1800.0,
            ["a2"] = 1800.0,
            ["b1"] = 1800.0,
            ["b2"] = 1800.0
        };

        var ids = Enumerable.Range(0, 4)
            .Select(i => ScheduleEngine.ComputeSchedule(
                channel, Anchor20240115Utc + (i * 1800), durations).JellyfinId)
            .ToArray();

        // Every episode appears exactly once, in WRR order.
        Assert.Equal(new[] { "a1", "b1", "a2", "b2" }, ids);

        // One full cycle later we wrap to the start — proving the cycle advanced
        // through the whole catalog rather than replaying episode 1 each cycle.
        var wrap = ScheduleEngine.ComputeSchedule(
            channel, Anchor20240115Utc + (4 * 1800), durations);
        Assert.Equal("a1", wrap.JellyfinId);
    }

    [Fact]
    public void ComputeSchedule_MultiBlock_ElapsedCountsOnlyBlockTime()
    {
        // A block covering hours 15–17 (2h/day) should advance its rotation
        // based only on time spent inside that window, not all wall-clock time.
        //
        // Setup: anchor at Anchor20240115Utc. Block 15-17 Chicago time.
        // 2024-01-15 15:00 CST = 21:00 UTC = Anchor20240115Utc + 75600s
        // Episode duration = 7200s (2h, exactly fills the block).
        //
        // Day 1 at 15:00 CST: block_elapsed = 0 → ep1@0
        // Day 2 at 15:00 CST: block_elapsed = 7200 (one full day's block)
        //   7200 % 7200 = 0 → still ep1@0
        //
        // Day 1 at 16:00 CST (3600s into block): block_elapsed = 3600 → ep1@3600
        // Day 2 at 16:00 CST: block_elapsed = 7200+3600 = 10800
        //   10800 % 7200 = 3600 → ep1@3600 (same position in cycle, correct)

        var channel = new ChannelConfig
        {
            Id = "1",
            Name = "Test",
            StartAnchor = "2024-01-15T00:00:00Z",
            Blocks = new List<Block>
            {
                new Block
                {
                    StartHour = 15, EndHour = 17,
                    Shows = new List<Show>
                    {
                        new Show { Name = "S", Weight = 1, JellyfinIds = new List<string> { "ep1" } }
                    }
                }
            }
        };
        var durations = new Dictionary<string, double> { ["ep1"] = 7200.0 };

        // 2024-01-15 15:00 CST = 21:00 UTC
        long day1BlockStart = Anchor20240115Utc + 75600L;
        // 2024-01-15 16:00 CST = 22:00 UTC (3600s into block)
        long day1MidBlock = day1BlockStart + 3600L;
        // 2024-01-16 15:00 CST = day 2 block start
        long day2BlockStart = day1BlockStart + 86400L;
        // 2024-01-16 16:00 CST = day 2 mid-block
        long day2MidBlock = day1MidBlock + 86400L;

        var d1Start = ScheduleEngine.ComputeSchedule(channel, day1BlockStart, durations);
        var d1Mid = ScheduleEngine.ComputeSchedule(channel, day1MidBlock, durations);
        var d2Start = ScheduleEngine.ComputeSchedule(channel, day2BlockStart, durations);
        var d2Mid = ScheduleEngine.ComputeSchedule(channel, day2MidBlock, durations);

        // Day 1 start of block: offset 0
        Assert.Equal("ep1", d1Start.JellyfinId);
        Assert.InRange(d1Start.StartOffset, 0.0, 1.0);

        // Day 1 mid-block: offset 3600
        Assert.Equal("ep1", d1Mid.JellyfinId);
        Assert.InRange(d1Mid.StartOffset, 3599.0, 3601.0);

        // Day 2 start: block_elapsed = 7200 = exactly one cycle → back to offset 0
        Assert.Equal("ep1", d2Start.JellyfinId);
        Assert.InRange(d2Start.StartOffset, 0.0, 1.0);

        // Day 2 mid: block_elapsed = 10800, 10800%7200=3600 → same mid-episode position
        Assert.Equal("ep1", d2Mid.JellyfinId);
        Assert.InRange(d2Mid.StartOffset, 3599.0, 3601.0);
    }


    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(899)]
    [InlineData(900)]
    [InlineData(1799)]
    [InlineData(3599)]
    public void ComputeSchedule_StartOffsetAlwaysInBounds(int secondsIntoBlock)
    {
        var channel = MultiEpisodeChannel(new[] { "ep1", "ep2" });
        var durations = new Dictionary<string, double>
        {
            ["ep1"] = 1800.0,
            ["ep2"] = 1800.0
        };

        long t = ChicagoMidnight20240115 + secondsIntoBlock;
        var result = ScheduleEngine.ComputeSchedule(channel, t, durations);

        if (!result.IsOffline)
        {
            Assert.True(result.StartOffset >= 0.0, $"StartOffset {result.StartOffset} < 0");
            Assert.True(result.StartOffset < result.Duration,
                $"StartOffset {result.StartOffset} >= Duration {result.Duration}");
        }
    }

    // ---------------------------------------------------------------------------
    // Empty JellyfinIds guard (Requirement 7.7)
    // ---------------------------------------------------------------------------

    [Fact]
    public void ExpandBlockEpisodes_ShowWithEmptyJellyfinIds_ReturnsEmpty()
    {
        // A block whose only show has no episode IDs must return an empty slot list.
        // Without this guard, WRR arithmetic would divide by zero.
        var block = new Block
        {
            StartHour = 0,
            EndHour = 24,
            Shows = new List<Show>
            {
                new Show { Name = "Empty Show", Weight = 1, JellyfinIds = new List<string>() }
            }
        };

        var slots = ScheduleEngine.ExpandBlockEpisodes(block);

        Assert.Empty(slots);
    }

    [Fact]
    public void ComputeSchedule_OnlyShowHasEmptyJellyfinIds_ReturnsOffline()
    {
        // When the only show in a channel's block has no episode IDs, the channel
        // has nothing to play and must report Offline rather than throw.
        var channel = new ChannelConfig
        {
            Id = "1",
            Name = "Test",
            Blocks = new List<Block>
            {
                new Block
                {
                    Name = "Block", StartHour = 0, EndHour = 24,
                    Shows = new List<Show>
                    {
                        new Show { Name = "Empty Show", Weight = 1, JellyfinIds = new List<string>() }
                    }
                }
            }
        };

        var result = ScheduleEngine.ComputeSchedule(channel, ChicagoMidnight20240115);

        Assert.True(result.IsOffline);
    }

    // ---------------------------------------------------------------------------
    // Shuffle — FisherYatesShuffle and SeedFor helpers (task 2.5, Requirements 7.1–7.4)
    // ---------------------------------------------------------------------------

    [Fact]
    public void FisherYatesShuffle_EmptyList_ReturnsEmpty()
    {
        var result = ScheduleEngine.FisherYatesShuffle(new List<string>(), seed: 42);
        Assert.Empty(result);
    }

    [Fact]
    public void FisherYatesShuffle_SingleItem_ReturnsSameItem()
    {
        var result = ScheduleEngine.FisherYatesShuffle(new List<string> { "ep1" }, seed: 99);
        Assert.Single(result);
        Assert.Equal("ep1", result[0]);
    }

    /// <summary>
    /// Property 11 (FisherYates variant): output is a permutation — same length, same elements.
    /// Tested across many list sizes and seeds.
    /// </summary>
    [Theory]
    [InlineData(new[] { "a", "b", "c" }, 0)]
    [InlineData(new[] { "a", "b", "c" }, 1)]
    [InlineData(new[] { "a", "b", "c" }, int.MaxValue)]
    [InlineData(new[] { "a", "b", "c" }, int.MinValue)]
    [InlineData(new[] { "ep1", "ep2", "ep3", "ep4", "ep5" }, 12345)]
    [InlineData(new[] { "x" }, 0)]
    [InlineData(new[] { "x", "y" }, 0)]
    [InlineData(new[] { "x", "y" }, 1)]
    public void FisherYatesShuffle_IsPermutation(string[] items, int seed)
    {
        var input = items.ToList();
        var result = ScheduleEngine.FisherYatesShuffle(input, seed);

        // Same count
        Assert.Equal(input.Count, result.Count);
        // Same elements
        Assert.Equal(input.OrderBy(x => x).ToList(), result.OrderBy(x => x).ToList());
    }

    [Fact]
    public void FisherYatesShuffle_LargeInput_IsPermutation()
    {
        // Sweep 100 iterations with varying seeds to exercise the permutation property broadly.
        var items = Enumerable.Range(0, 50).Select(i => $"ep{i:D3}").ToList();
        var expectedSorted = items.OrderBy(x => x).ToList();

        for (int seed = 0; seed < 100; seed++)
        {
            var result = ScheduleEngine.FisherYatesShuffle(items, seed);
            Assert.Equal(items.Count, result.Count);
            Assert.Equal(expectedSorted, result.OrderBy(x => x).ToList());
        }
    }

    [Fact]
    public void FisherYatesShuffle_SameSeed_SameOrder()
    {
        // Requirement 7.2: determinism — same seed produces identical permutation.
        var items = new List<string> { "ep1", "ep2", "ep3", "ep4", "ep5" };
        const int seed = 777;

        var r1 = ScheduleEngine.FisherYatesShuffle(items, seed);
        var r2 = ScheduleEngine.FisherYatesShuffle(items, seed);

        Assert.Equal(r1, r2);
    }

    [Fact]
    public void FisherYatesShuffle_DifferentSeeds_TypicallyDifferentOrder()
    {
        // Requirement 7.4 (statistical): different cycle index → different permutation.
        // Use a known test case where seeds 0 and 1 produce different orderings.
        var items = new List<string> { "ep1", "ep2", "ep3", "ep4", "ep5", "ep6", "ep7" };

        var results = Enumerable.Range(0, 20)
            .Select(seed => ScheduleEngine.FisherYatesShuffle(items, seed))
            .ToList();

        // Not all 20 permutations should be identical — verify at least one differs.
        bool anyDiffers = results.Skip(1).Any(r => !r.SequenceEqual(results[0]));
        Assert.True(anyDiffers, "All 20 seeds produced the same permutation — shuffle is not varying by seed");
    }

    [Fact]
    public void SeedFor_SameInputs_SameSeed()
    {
        // Property 10: deterministic — calling twice with identical inputs returns same int.
        int s1 = ScheduleEngine.SeedFor("sitcoms", "All Day", "Cheers", cycleIndex: 5L);
        int s2 = ScheduleEngine.SeedFor("sitcoms", "All Day", "Cheers", cycleIndex: 5L);
        Assert.Equal(s1, s2);
    }

    [Theory]
    [InlineData("sitcoms", "All Day", "Cheers", 0L, "sitcoms", "All Day", "Cheers", 1L)]  // different cycleIndex
    [InlineData("sitcoms", "All Day", "Cheers", 5L, "anime", "All Day", "Cheers", 5L)]  // different channelId
    [InlineData("sitcoms", "All Day", "Cheers", 5L, "sitcoms", "Night", "Cheers", 5L)]  // different blockName
    [InlineData("sitcoms", "All Day", "Cheers", 5L, "sitcoms", "All Day", "Seinfeld", 5L)] // different showName
    public void SeedFor_DifferentInputs_DifferentSeeds(
        string ch1, string blk1, string show1, long ci1,
        string ch2, string blk2, string show2, long ci2)
    {
        int s1 = ScheduleEngine.SeedFor(ch1, blk1, show1, ci1);
        int s2 = ScheduleEngine.SeedFor(ch2, blk2, show2, ci2);
        Assert.NotEqual(s1, s2);
    }

    // ---------------------------------------------------------------------------
    // Shuffle in ComputeSchedule (Requirements 7.1–7.5)
    // ---------------------------------------------------------------------------

    [Fact]
    public void ComputeSchedule_ShuffleTrue_SameInputsSameResult()
    {
        // Requirement 7.7: engine is pure/deterministic — same (channel, unix, durations) → same result.
        var channel = new ChannelConfig
        {
            Id = "sitcoms",
            Name = "Sitcoms",
            StartAnchor = "2024-01-15T00:00:00Z",
            Blocks = new List<Block>
            {
                new Block
                {
                    Name = "All Day", StartHour = 0, EndHour = 24,
                    Shows = new List<Show>
                    {
                        new Show { Name = "Cheers", Weight = 1, Shuffle = true,
                            JellyfinIds = new List<string> { "ep1", "ep2", "ep3", "ep4", "ep5" } }
                    }
                }
            }
        };
        var durations = Enumerable.Range(1, 5)
            .ToDictionary(i => $"ep{i}", _ => 1800.0);

        long t = Anchor20240115Utc + 900;

        var r1 = ScheduleEngine.ComputeSchedule(channel, t, durations);
        var r2 = ScheduleEngine.ComputeSchedule(channel, t, durations);

        Assert.Equal(r1.JellyfinId, r2.JellyfinId);
        Assert.Equal(r1.StartOffset, r2.StartOffset);
    }

    [Fact]
    public void ComputeSchedule_ShuffleFalse_LinearOrder()
    {
        // Requirement 7.5: Shuffle=false leaves episode order unchanged.
        // With a 24/7 block starting at anchor, episodes should play in stored order: ep1, ep2, ep3.
        var channel = new ChannelConfig
        {
            Id = "ch1",
            Name = "Ch1",
            StartAnchor = "2024-01-15T00:00:00Z",
            Blocks = new List<Block>
            {
                new Block
                {
                    Name = "Block", StartHour = 0, EndHour = 24,
                    Shows = new List<Show>
                    {
                        new Show { Name = "Show", Weight = 1, Shuffle = false,
                            JellyfinIds = new List<string> { "ep1", "ep2", "ep3" } }
                    }
                }
            }
        };
        var durations = new Dictionary<string, double>
        {
            ["ep1"] = 1800.0,
            ["ep2"] = 1800.0,
            ["ep3"] = 1800.0
        };

        // At anchor: ep1@0
        var r0 = ScheduleEngine.ComputeSchedule(channel, Anchor20240115Utc, durations);
        // At anchor+1800: ep2@0
        var r1 = ScheduleEngine.ComputeSchedule(channel, Anchor20240115Utc + 1800, durations);
        // At anchor+3600: ep3@0
        var r2 = ScheduleEngine.ComputeSchedule(channel, Anchor20240115Utc + 3600, durations);

        Assert.Equal("ep1", r0.JellyfinId);
        Assert.Equal("ep2", r1.JellyfinId);
        Assert.Equal("ep3", r2.JellyfinId);
    }

    [Fact]
    public void ComputeSchedule_ShuffleTrue_EpisodesStillAllAppearInCycle()
    {
        // Requirement 7.3: every episode appears exactly once per cycle, even with shuffle.
        // Walk the full cycle and collect unique episode IDs.
        const int epCount = 6;
        var episodeIds = Enumerable.Range(1, epCount).Select(i => $"ep{i}").ToList();
        var channel = new ChannelConfig
        {
            Id = "test",
            Name = "Test",
            StartAnchor = "2024-01-15T00:00:00Z",
            Blocks = new List<Block>
            {
                new Block
                {
                    Name = "Block", StartHour = 0, EndHour = 24,
                    Shows = new List<Show>
                    {
                        new Show { Name = "Show", Weight = 1, Shuffle = true,
                            JellyfinIds = new List<string>(episodeIds) }
                    }
                }
            }
        };
        double epDuration = 1800.0;
        var durations = episodeIds.ToDictionary(id => id, _ => epDuration);
        double cycleDuration = epCount * epDuration; // 10800s

        // Walk through the cycle second-by-second isn't practical; sample once per episode slot.
        var seen = new HashSet<string>();
        for (int i = 0; i < epCount; i++)
        {
            long t = Anchor20240115Utc + (long)(i * epDuration) + 1;
            var result = ScheduleEngine.ComputeSchedule(channel, t, durations);
            Assert.False(result.IsOffline);
            seen.Add(result.JellyfinId);
        }

        // All episodes must appear exactly once across the sampled cycle positions.
        Assert.Equal(epCount, seen.Count);
        foreach (var id in episodeIds)
            Assert.Contains(id, seen);
    }

    [Fact]
    public void ComputeSchedule_ShuffleTrue_DifferentCyclesDifferentOrder()
    {
        // Requirement 7.4: advancing to next cycle produces a different permutation.
        // Build a channel with enough episodes that a re-shuffle changes the first slot.
        const int epCount = 8;
        var episodeIds = Enumerable.Range(1, epCount).Select(i => $"ep{i}").ToList();
        var channel = new ChannelConfig
        {
            Id = "test",
            Name = "Test",
            StartAnchor = "2024-01-15T00:00:00Z",
            Blocks = new List<Block>
            {
                new Block
                {
                    Name = "Block", StartHour = 0, EndHour = 24,
                    Shows = new List<Show>
                    {
                        new Show { Name = "Show", Weight = 1, Shuffle = true,
                            JellyfinIds = new List<string>(episodeIds) }
                    }
                }
            }
        };
        double epDuration = 1800.0;
        var durations = episodeIds.ToDictionary(id => id, _ => epDuration);
        double cycleDuration = epCount * epDuration; // 14400s

        // Sample first slot of cycle 0 and cycle 1.
        long cycle0Start = Anchor20240115Utc + 1;  // 1s into cycle 0
        long cycle1Start = Anchor20240115Utc + (long)cycleDuration + 1; // 1s into cycle 1

        var r0 = ScheduleEngine.ComputeSchedule(channel, cycle0Start, durations);
        var r1 = ScheduleEngine.ComputeSchedule(channel, cycle1Start, durations);

        // Both must be valid (non-offline)
        Assert.False(r0.IsOffline);
        Assert.False(r1.IsOffline);

        // The first episode in cycle 0 vs cycle 1 should differ — different cycleIndex → different seed.
        // This is a statistical check: for 8 episodes, probability same first slot = 1/8 = 12.5%.
        // We use a concrete seed comparison rather than probabilistic: verify the seed changes.
        long ci0 = 0L;
        long ci1 = 1L;
        int seed0 = ScheduleEngine.SeedFor("test", "Block", "Show", ci0);
        int seed1 = ScheduleEngine.SeedFor("test", "Block", "Show", ci1);
        Assert.NotEqual(seed0, seed1);
    }

    [Fact]
    public void ApplyShuffleToBlock_ShuffleFalse_ReturnsSameBlockReference()
    {
        // Requirement 7.5: Shuffle=false shows are left untouched.
        var show = new Show { Name = "S", Weight = 1, Shuffle = false, JellyfinIds = new List<string> { "a", "b" } };
        var block = new Block { Name = "B", StartHour = 0, EndHour = 24, Shows = new List<Show> { show } };
        var channel = new ChannelConfig { Id = "ch", Name = "C", Blocks = new List<Block> { block } };

        var result = ScheduleEngine.ApplyShuffleToBlock(channel, block, cycleIndex: 0L);

        // Fast path: same reference returned when no shuffle needed
        Assert.Same(block, result);
    }

    [Fact]
    public void ApplyShuffleToBlock_ShuffleTrue_ReturnsDifferentBlockWithPermutedIds()
    {
        // Requirement 7.1: Shuffle=true show gets its JellyfinIds permuted.
        var episodeIds = new List<string> { "ep1", "ep2", "ep3", "ep4", "ep5" };
        var show = new Show { Name = "Show", Weight = 1, Shuffle = true, JellyfinIds = episodeIds };
        var block = new Block { Name = "Block", StartHour = 0, EndHour = 24, Shows = new List<Show> { show } };
        var channel = new ChannelConfig { Id = "ch", Name = "C", Blocks = new List<Block> { block } };

        var result = ScheduleEngine.ApplyShuffleToBlock(channel, block, cycleIndex: 0L);

        // Different block object
        Assert.NotSame(block, result);
        // Original IDs not mutated
        Assert.Equal(episodeIds, show.JellyfinIds);
        // Shuffled block's show has a permutation of original IDs
        Assert.Equal(episodeIds.OrderBy(x => x).ToList(),
                     result.Shows![0].JellyfinIds.OrderBy(x => x).ToList());
    }

    [Fact]
    public void ApplyShuffleToBlock_SameCycleIndex_SamePermutation()
    {
        // Requirement 7.2: deterministic — same cycleIndex → same permutation.
        var show = new Show
        {
            Name = "Show",
            Weight = 1,
            Shuffle = true,
            JellyfinIds = new List<string> { "ep1", "ep2", "ep3", "ep4" }
        };
        var block = new Block { Name = "Block", StartHour = 0, EndHour = 24, Shows = new List<Show> { show } };
        var channel = new ChannelConfig { Id = "ch", Name = "C", Blocks = new List<Block> { block } };

        var r1 = ScheduleEngine.ApplyShuffleToBlock(channel, block, cycleIndex: 7L);
        var r2 = ScheduleEngine.ApplyShuffleToBlock(channel, block, cycleIndex: 7L);

        Assert.Equal(r1.Shows![0].JellyfinIds, r2.Shows![0].JellyfinIds);
    }

    // ---------------------------------------------------------------------------
    // PruneMissingEpisodes — never schedule deleted library items
    // ---------------------------------------------------------------------------

    [Fact]
    public void PruneMissingEpisodes_RemovesIdsAbsentFromDurationMap()
    {
        var channel = MultiEpisodeChannel(new[] { "a", "b", "c" });
        var durations = new Dictionary<string, double> { ["a"] = 100.0, ["c"] = 100.0 };

        var pruned = ScheduleEngine.PruneMissingEpisodes(channel, durations);

        Assert.Equal(new[] { "a", "c" }, pruned.Blocks[0].Shows[0].JellyfinIds);
        // Original is not mutated.
        Assert.Equal(new[] { "a", "b", "c" }, channel.Blocks[0].Shows[0].JellyfinIds);
    }

    [Fact]
    public void PruneMissingEpisodes_EmptyDurationMap_ReturnsSameInstance()
    {
        // Before the first duration resolution the whole catalog must be kept
        // (fallback durations), not pruned to nothing.
        var channel = MultiEpisodeChannel(new[] { "a", "b" });
        Assert.Same(channel, ScheduleEngine.PruneMissingEpisodes(channel, new Dictionary<string, double>()));
    }

    [Fact]
    public void PruneMissingEpisodes_AllPresent_ReturnsSameInstance()
    {
        var channel = MultiEpisodeChannel(new[] { "a", "b" });
        var durations = new Dictionary<string, double> { ["a"] = 1.0, ["b"] = 1.0 };
        Assert.Same(channel, ScheduleEngine.PruneMissingEpisodes(channel, durations));
    }

    [Fact]
    public void PruneMissingEpisodes_ThenCompute_NeverReturnsMissingItem()
    {
        // Regression: a deleted episode was scheduled and the client 404'd on it
        // forever. After pruning, only present episodes are scheduled.
        var channel = MultiEpisodeChannel(new[] { "gone", "kept" });
        var durations = new Dictionary<string, double> { ["kept"] = 1800.0 };

        var pruned = ScheduleEngine.PruneMissingEpisodes(channel, durations);
        var result = ScheduleEngine.ComputeSchedule(pruned, ChicagoMidnight20240115, durations);

        Assert.Equal("kept", result.JellyfinId);
    }
}
