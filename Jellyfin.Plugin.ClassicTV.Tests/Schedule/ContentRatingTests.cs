using Jellyfin.Plugin.ClassicTV.Api;
using Jellyfin.Plugin.ClassicTV.Schedule;
using Xunit;

namespace Jellyfin.Plugin.ClassicTV.Tests.Schedule;

public sealed class ContentRatingTests
{
    [Theory]
    [InlineData("R", null, false, true)]
    [InlineData("R", "", false, true)]
    [InlineData("G", "G", false, true)]
    [InlineData("TV-Y7", "G", false, true)]
    [InlineData("PG", "G", false, false)]
    [InlineData("TV-PG", "PG", false, true)]
    [InlineData("TV-14", "PG", false, false)]
    [InlineData("TV-14", "PG-13", false, true)]
    [InlineData("R", "PG-13", false, false)]
    [InlineData("TV-MA", "R", false, true)]
    [InlineData("NC-17", "R", false, false)]
    [InlineData(null, "PG", false, false)]
    [InlineData("NR", "PG", false, false)]
    [InlineData(null, "PG", true, true)]
    [InlineData("NR", "PG", true, true)]
    public void IsAllowed_RespectsNormalizedScaleAndUnratedFlag(
        string? itemRating, string? maxRating, bool allowUnrated, bool expected)
    {
        Assert.Equal(expected, ContentRating.IsAllowed(itemRating, maxRating, allowUnrated));
    }

    [Fact]
    public void ChannelConfig_AllowUnratedDefaultsToTrue()
    {
        var ch = new ChannelConfig();
        Assert.True(ch.AllowUnrated);
    }

    [Fact]
    public void PruneByRating_FiltersOverRatingAndRespectsAllowUnrated()
    {
        var channel = new ChannelConfig
        {
            Id = "movies",
            Name = "Movies",
            MaxRating = "PG",
            AllowUnrated = false,
            Blocks = new List<Block>
            {
                new()
                {
                    Name = "All Day",
                    StartHour = 0,
                    EndHour = 24,
                    Shows = new List<Show>
                    {
                        new()
                        {
                            Name = "Mix",
                            JellyfinIds = new List<string> { "g-id", "pg-id", "r-id", "unrated-id" }
                        }
                    }
                }
            }
        };

        var ratings = new Dictionary<string, string?>
        {
            ["g-id"] = "G",
            ["pg-id"] = "TV-PG",
            ["r-id"] = "R",
            ["unrated-id"] = null
        };

        var pruned = ScheduleEngine.PruneByRating(channel, ratings);
        Assert.Equal(new[] { "g-id", "pg-id" }, pruned.Blocks[0].Shows![0].JellyfinIds);

        channel.AllowUnrated = true;
        var prunedWithUnrated = ScheduleEngine.PruneByRating(channel, ratings);
        Assert.Equal(new[] { "g-id", "pg-id", "unrated-id" }, prunedWithUnrated.Blocks[0].Shows![0].JellyfinIds);
    }

    [Fact]
    public void Validator_RejectsInvalidMaxRating()
    {
        var root = new ChannelConfigRoot
        {
            Channels = new List<ChannelConfig>
            {
                new()
                {
                    Id = "kids",
                    Name = "Kids",
                    Number = 2,
                    MaxRating = "PG-99",
                    Blocks = new List<Block>
                    {
                        new() { Name = "All", StartHour = 0, EndHour = 24, Shows = new List<Show>() }
                    }
                }
            }
        };

        var errors = ChannelConfigValidator.Validate(root);
        Assert.Contains(errors, e => e.Contains("MaxRating", StringComparison.Ordinal));
    }
}
