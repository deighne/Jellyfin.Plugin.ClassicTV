using System.Collections.Generic;
using Jellyfin.Plugin.ClassicTV.Api;
using Jellyfin.Plugin.ClassicTV.Schedule;
using Xunit;

namespace Jellyfin.Plugin.ClassicTV.Tests.Api;

/// <summary>
/// Tests for <see cref="ChannelConfigValidator"/>.
///
/// Covers:
///   - Empty channel ID → error
///   - ID pattern violations → error
///   - Duplicate channel IDs → error
///   - Channel number out of range → error
///   - Duplicate non-zero number among enabled channels → error
///   - Block startHour / endHour / endHour > startHour → error
///   - Block overlap within a channel → error
///   - Adjacent blocks (shared boundary, no gap) → NO error (regression guard)
///   - Offline blocks exempt from hour validation → no error
///   - Valid complete config → no errors
///   - Empty channel list → no errors (clearing all channels is allowed)
/// </summary>
public sealed class ChannelConfigValidatorTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ChannelConfig ValidChannel(string id = "ch-1", int number = 1) => new()
    {
        Id = id,
        Name = $"Channel {id}",
        Number = number,
        Enabled = true,
        Blocks = new List<Block>
        {
            new() { Name = "All Day", StartHour = 0, EndHour = 24, Offline = false, Shows = new() }
        }
    };

    private static ChannelConfigRoot Wrap(params ChannelConfig[] channels) =>
        new() { Channels = new List<ChannelConfig>(channels) };

    // -------------------------------------------------------------------------
    // Empty / null inputs
    // -------------------------------------------------------------------------

    [Fact]
    public void Validate_EmptyChannelList_ReturnsNoErrors()
    {
        var root = new ChannelConfigRoot { Channels = new List<ChannelConfig>() };
        var errors = ChannelConfigValidator.Validate(root);
        Assert.Empty(errors);
    }

    // -------------------------------------------------------------------------
    // Channel ID — format and uniqueness
    // -------------------------------------------------------------------------

    [Fact]
    public void Validate_EmptyChannelId_ReturnsError()
    {
        var ch = ValidChannel();
        ch.Id = string.Empty;
        var errors = ChannelConfigValidator.Validate(Wrap(ch));
        Assert.Contains(errors, e => e.Contains("must not be empty"));
    }

    [Fact]
    public void Validate_WhitespaceChannelId_ReturnsError()
    {
        var ch = ValidChannel();
        ch.Id = "   ";
        var errors = ChannelConfigValidator.Validate(Wrap(ch));
        Assert.Contains(errors, e => e.Contains("must not be empty"));
    }

    [Theory]
    [InlineData("My Channel")]          // spaces not allowed
    [InlineData("UPPER")]               // uppercase not allowed
    [InlineData("ch_1")]                // underscore not allowed
    [InlineData("ch.1")]                // dot not allowed
    [InlineData("ch/1")]                // slash not allowed
    public void Validate_InvalidIdPattern_ReturnsError(string badId)
    {
        var ch = ValidChannel();
        ch.Id = badId;
        var errors = ChannelConfigValidator.Validate(Wrap(ch));
        Assert.NotEmpty(errors);
    }

    [Theory]
    [InlineData("ch1")]
    [InlineData("ch-1")]
    [InlineData("abc")]
    [InlineData("my-channel")]
    [InlineData("channel-123")]
    [InlineData("a")]
    [InlineData("1")]
    public void Validate_ValidIdPatterns_ProducesNoIdError(string goodId)
    {
        var ch = ValidChannel(goodId);
        var errors = ChannelConfigValidator.Validate(Wrap(ch));
        // Should be no errors related to ID format specifically.
        Assert.DoesNotContain(errors, e => e.Contains("is invalid") && e.Contains("must match"));
    }

    [Fact]
    public void Validate_DuplicateChannelIds_ReturnsError()
    {
        var ch1 = ValidChannel("movies", 1);
        var ch2 = ValidChannel("movies", 2);
        var errors = ChannelConfigValidator.Validate(Wrap(ch1, ch2));
        Assert.Contains(errors, e => e.Contains("duplicated"));
    }

    [Fact]
    public void Validate_UniqueChannelIds_ProducesNoDuplicateError()
    {
        var ch1 = ValidChannel("news", 1);
        var ch2 = ValidChannel("movies", 2);
        var errors = ChannelConfigValidator.Validate(Wrap(ch1, ch2));
        Assert.DoesNotContain(errors, e => e.Contains("duplicated"));
    }

    // -------------------------------------------------------------------------
    // Channel number — range
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(-1)]
    [InlineData(10000)]
    [InlineData(99999)]
    public void Validate_NumberOutOfRange_ReturnsError(int badNumber)
    {
        var ch = ValidChannel("ch-x", badNumber);
        var errors = ChannelConfigValidator.Validate(Wrap(ch));
        Assert.Contains(errors, e => e.Contains("out of range"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9999)]
    public void Validate_NumberInRange_ProducesNoRangeError(int goodNumber)
    {
        var ch = ValidChannel("ch-x", goodNumber);
        var errors = ChannelConfigValidator.Validate(Wrap(ch));
        Assert.DoesNotContain(errors, e => e.Contains("out of range"));
    }

    // -------------------------------------------------------------------------
    // Channel number — uniqueness among enabled channels
    // -------------------------------------------------------------------------

    [Fact]
    public void Validate_DuplicateNumberAmongEnabledChannels_ReturnsError()
    {
        var ch1 = ValidChannel("news", 5);
        var ch2 = ValidChannel("movies", 5);
        var errors = ChannelConfigValidator.Validate(Wrap(ch1, ch2));
        Assert.Contains(errors, e => e.Contains("already assigned") && e.Contains("5"));
    }

    [Fact]
    public void Validate_DuplicateNumberZero_IsAllowed()
    {
        // Number = 0 means unassigned; duplicates are fine.
        var ch1 = ValidChannel("news", 0);
        var ch2 = ValidChannel("movies", 0);
        var errors = ChannelConfigValidator.Validate(Wrap(ch1, ch2));
        Assert.DoesNotContain(errors, e => e.Contains("already assigned"));
    }

    [Fact]
    public void Validate_DuplicateNumberOneDisabled_IsAllowed()
    {
        // Duplicate numbers are only an error when BOTH channels are enabled.
        var ch1 = ValidChannel("news", 5);
        var ch2 = ValidChannel("movies", 5);
        ch2.Enabled = false;
        var errors = ChannelConfigValidator.Validate(Wrap(ch1, ch2));
        Assert.DoesNotContain(errors, e => e.Contains("already assigned"));
    }

    // -------------------------------------------------------------------------
    // Block hour ranges
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(-1, 12)]    // startHour below 0
    [InlineData(24, 25)]    // startHour above 23
    public void Validate_StartHourOutOfRange_ReturnsError(int start, int end)
    {
        var ch = ValidChannel();
        ch.Blocks = new List<Block>
        {
            new() { Name = "BadStart", StartHour = start, EndHour = end, Offline = false, Shows = new() }
        };
        var errors = ChannelConfigValidator.Validate(Wrap(ch));
        Assert.Contains(errors, e => e.Contains("startHour") && e.Contains("out of range"));
    }

    [Theory]
    [InlineData(0, 0)]      // endHour below 1
    [InlineData(0, 25)]     // endHour above 24
    public void Validate_EndHourOutOfRange_ReturnsError(int start, int end)
    {
        var ch = ValidChannel();
        ch.Blocks = new List<Block>
        {
            new() { Name = "BadEnd", StartHour = start, EndHour = end, Offline = false, Shows = new() }
        };
        var errors = ChannelConfigValidator.Validate(Wrap(ch));
        Assert.Contains(errors, e => e.Contains("endHour") && e.Contains("out of range"));
    }

    [Fact]
    public void Validate_EndHourNotGreaterThanStartHour_ReturnsError()
    {
        var ch = ValidChannel();
        ch.Blocks = new List<Block>
        {
            new() { Name = "Bad", StartHour = 12, EndHour = 12, Offline = false, Shows = new() }
        };
        var errors = ChannelConfigValidator.Validate(Wrap(ch));
        Assert.Contains(errors, e => e.Contains("strictly greater than"));
    }

    [Fact]
    public void Validate_EndHourBeforeStartHour_ReturnsError()
    {
        var ch = ValidChannel();
        ch.Blocks = new List<Block>
        {
            new() { Name = "Reversed", StartHour = 18, EndHour = 6, Offline = false, Shows = new() }
        };
        var errors = ChannelConfigValidator.Validate(Wrap(ch));
        Assert.Contains(errors, e => e.Contains("strictly greater than"));
    }

    [Fact]
    public void Validate_OfflineBlock_IsExemptFromHourRangeValidation()
    {
        // Offline blocks with out-of-range hours must not produce hour errors.
        var ch = ValidChannel();
        ch.Blocks = new List<Block>
        {
            new() { Name = "Offline", StartHour = -99, EndHour = -1, Offline = true, Shows = null }
        };
        var errors = ChannelConfigValidator.Validate(Wrap(ch));
        Assert.DoesNotContain(errors, e => e.Contains("startHour") || e.Contains("endHour") || e.Contains("strictly greater than"));
    }

    // -------------------------------------------------------------------------
    // Block overlap
    // -------------------------------------------------------------------------

    [Fact]
    public void Validate_OverlappingBlocks_ReturnsError()
    {
        // [6, 18) and [12, 24) overlap.
        var ch = ValidChannel();
        ch.Blocks = new List<Block>
        {
            new() { Name = "Morning", StartHour = 6, EndHour = 18, Offline = false, Shows = new() },
            new() { Name = "Evening", StartHour = 12, EndHour = 24, Offline = false, Shows = new() },
        };
        var errors = ChannelConfigValidator.Validate(Wrap(ch));
        Assert.Contains(errors, e => e.Contains("overlaps"));
    }

    [Fact]
    public void Validate_FullyContainedBlock_ReturnsError()
    {
        // [0, 24) fully contains [8, 16).
        var ch = ValidChannel();
        ch.Blocks = new List<Block>
        {
            new() { Name = "AllDay", StartHour = 0, EndHour = 24, Offline = false, Shows = new() },
            new() { Name = "Midday", StartHour = 8, EndHour = 16, Offline = false, Shows = new() },
        };
        var errors = ChannelConfigValidator.Validate(Wrap(ch));
        Assert.Contains(errors, e => e.Contains("overlaps"));
    }

    [Fact]
    public void Validate_AdjacentBlocks_DoNotOverlap()
    {
        // [0, 12) and [12, 24) share only a boundary — must NOT be flagged as overlapping.
        var ch = ValidChannel();
        ch.Blocks = new List<Block>
        {
            new() { Name = "Morning", StartHour = 0, EndHour = 12, Offline = false, Shows = new() },
            new() { Name = "Afternoon", StartHour = 12, EndHour = 24, Offline = false, Shows = new() },
        };
        var errors = ChannelConfigValidator.Validate(Wrap(ch));
        Assert.DoesNotContain(errors, e => e.Contains("overlaps"));
    }

    [Fact]
    public void Validate_ThreeAdjacentBlocks_DoNotOverlap()
    {
        // [0,8), [8,16), [16,24) — three adjacent blocks, no overlap.
        var ch = ValidChannel();
        ch.Blocks = new List<Block>
        {
            new() { Name = "Morning", StartHour = 0, EndHour = 8, Offline = false, Shows = new() },
            new() { Name = "Afternoon", StartHour = 8, EndHour = 16, Offline = false, Shows = new() },
            new() { Name = "Evening", StartHour = 16, EndHour = 24, Offline = false, Shows = new() },
        };
        var errors = ChannelConfigValidator.Validate(Wrap(ch));
        Assert.DoesNotContain(errors, e => e.Contains("overlaps"));
    }

    [Fact]
    public void Validate_OfflineBlockDoesNotParticipateInOverlapCheck()
    {
        // An offline block at [0,24) should not cause the active block [0,12) to be flagged.
        var ch = ValidChannel();
        ch.Blocks = new List<Block>
        {
            new() { Name = "Offline", StartHour = 0, EndHour = 24, Offline = true, Shows = null },
            new() { Name = "Active", StartHour = 0, EndHour = 12, Offline = false, Shows = new() },
        };
        var errors = ChannelConfigValidator.Validate(Wrap(ch));
        Assert.DoesNotContain(errors, e => e.Contains("overlaps"));
    }

    // -------------------------------------------------------------------------
    // Valid configurations → zero errors
    // -------------------------------------------------------------------------

    [Fact]
    public void Validate_FullyValidSingleChannel_ReturnsNoErrors()
    {
        var errors = ChannelConfigValidator.Validate(Wrap(ValidChannel("movies", 1)));
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_TwoValidChannels_ReturnsNoErrors()
    {
        var ch1 = ValidChannel("news", 1);
        var ch2 = ValidChannel("movies", 2);
        var errors = ChannelConfigValidator.Validate(Wrap(ch1, ch2));
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_ChannelWithMultipleNonOverlappingBlocks_ReturnsNoErrors()
    {
        var ch = new ChannelConfig
        {
            Id = "mixed",
            Name = "Mixed",
            Number = 3,
            Enabled = true,
            Blocks = new List<Block>
            {
                new() { Name = "Morning", StartHour = 6, EndHour = 12, Offline = false, Shows = new() },
                new() { Name = "Offline", StartHour = 0, EndHour = 6, Offline = true, Shows = null },
                new() { Name = "Evening", StartHour = 12, EndHour = 24, Offline = false, Shows = new() },
            }
        };
        var errors = ChannelConfigValidator.Validate(Wrap(ch));
        Assert.Empty(errors);
    }

    // -------------------------------------------------------------------------
    // Multiple errors reported simultaneously
    // -------------------------------------------------------------------------

    [Fact]
    public void Validate_MultipleViolations_AllErrorsReported()
    {
        // Empty ID + overlapping blocks in one channel.
        var ch = new ChannelConfig
        {
            Id = string.Empty,  // violation 1: empty ID
            Name = "Bad",
            Number = 1,
            Enabled = true,
            Blocks = new List<Block>
            {
                new() { Name = "A", StartHour = 0, EndHour = 18, Offline = false, Shows = new() },
                new() { Name = "B", StartHour = 12, EndHour = 24, Offline = false, Shows = new() }, // violation 2: overlap
            }
        };
        var errors = ChannelConfigValidator.Validate(Wrap(ch));
        Assert.True(errors.Count >= 2, $"Expected at least 2 errors, got {errors.Count}: {string.Join("; ", errors)}");
    }
}
