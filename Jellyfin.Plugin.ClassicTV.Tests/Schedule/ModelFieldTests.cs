using System.Text.Json;
using Jellyfin.Plugin.ClassicTV.Schedule;
using Xunit;

namespace Jellyfin.Plugin.ClassicTV.Tests.Schedule;

/// <summary>
/// Property tests for model field defaults (Requirement 5.4) and round-trip
/// serialization (Requirement 5.5).
///
/// These tests use System.Text.Json directly — no Jellyfin host is required.
/// </summary>
public sealed class ModelFieldTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    // -----------------------------------------------------------------------
    // Property 6: Deserialization applies defaults for missing fields
    //
    // For any combination of absent `number`, `enabled`, and `shuffle` keys,
    // the deserialized values must be 0, true, and false respectively.
    // -----------------------------------------------------------------------

    [Fact]
    public void ChannelConfig_MissingNumber_DefaultsToZero()
    {
        const string json = """{"id":"ch1","name":"Test","blocks":[]}""";
        var channel = JsonSerializer.Deserialize<ChannelConfig>(json, JsonOptions)!;
        Assert.Equal(0, channel.Number);
    }

    [Fact]
    public void ChannelConfig_MissingEnabled_DefaultsToTrue()
    {
        const string json = """{"id":"ch1","name":"Test","blocks":[]}""";
        var channel = JsonSerializer.Deserialize<ChannelConfig>(json, JsonOptions)!;
        Assert.True(channel.Enabled);
    }

    [Fact]
    public void Show_MissingShuffle_DefaultsToFalse()
    {
        const string json = """{"name":"My Show","weight":1,"jellyfinIds":[]}""";
        var show = JsonSerializer.Deserialize<Show>(json, JsonOptions)!;
        Assert.False(show.Shuffle);
    }

    /// <summary>
    /// Property 6 — iterates over all 8 combinations of absent/present
    /// `number`, `enabled`, and `shuffle` keys to confirm defaults apply
    /// regardless of which subset is missing.
    /// </summary>
    [Theory]
    [InlineData(false, false, false)] // all three absent
    [InlineData(true, false, false)] // number present, enabled+shuffle absent
    [InlineData(false, true, false)] // enabled present, number+shuffle absent
    [InlineData(false, false, true)]  // shuffle present, number+enabled absent
    [InlineData(true, true, false)] // shuffle absent only
    [InlineData(true, false, true)]  // enabled absent only
    [InlineData(false, true, true)]  // number absent only
    [InlineData(true, true, true)]  // all present (boundary: no defaults needed)
    public void DefaultsApplyForAllAbsentCombinations(
        bool includeNumber, bool includeEnabled, bool includeShuffle)
    {
        // Build the channel JSON dynamically based on which fields are included.
        var channelParts = new List<string>
        {
            @"""id"":""ch1""",
            @"""name"":""Test""",
            @"""blocks"":[{""name"":""B"",""startHour"":0,""endHour"":24,""shows"":[]}]"
        };
        if (includeNumber) channelParts.Add(@"""number"":7");
        if (includeEnabled) channelParts.Add(@"""enabled"":false");

        var channelJson = "{" + string.Join(",", channelParts) + "}";
        var channel = JsonSerializer.Deserialize<ChannelConfig>(channelJson, JsonOptions)!;

        if (includeNumber)
            Assert.Equal(7, channel.Number);
        else
            Assert.Equal(0, channel.Number);   // default

        if (includeEnabled)
            Assert.False(channel.Enabled);
        else
            Assert.True(channel.Enabled);      // default

        // Build the show JSON for the shuffle field.
        var showParts = new List<string>
        {
            @"""name"":""S""",
            @"""weight"":1",
            @"""jellyfinIds"":[""ep1""]"
        };
        if (includeShuffle) showParts.Add(@"""shuffle"":true");

        var showJson = "{" + string.Join(",", showParts) + "}";
        var show = JsonSerializer.Deserialize<Show>(showJson, JsonOptions)!;

        if (includeShuffle)
            Assert.True(show.Shuffle);
        else
            Assert.False(show.Shuffle);        // default
    }

    [Fact]
    public void ChannelConfigRoot_WithNoFieldsPresent_DeserializesWithAllDefaults()
    {
        // A realistic "old-format" channels-v4.json that pre-dates the new fields.
        const string json = """
            {
              "channels": [
                {
                  "id": "classic",
                  "name": "Classic",
                  "startAnchor": "2024-01-01T00:00:00Z",
                  "blocks": [
                    {
                      "name": "All Day",
                      "startHour": 0,
                      "endHour": 24,
                      "shows": [
                        {
                          "name": "The Show",
                          "weight": 2,
                          "jellyfinIds": ["id1", "id2"]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        var root = JsonSerializer.Deserialize<ChannelConfigRoot>(json, JsonOptions)!;

        Assert.Single(root.Channels);
        var channel = root.Channels[0];
        Assert.Equal(0, channel.Number);   // absent → default 0
        Assert.True(channel.Enabled);      // absent → default true

        Assert.Single(channel.Blocks);
        var show = channel.Blocks[0].Shows![0];
        Assert.False(show.Shuffle);        // absent → default false
    }

    // -----------------------------------------------------------------------
    // Property 7: Serialization round-trip preserves all fields
    //
    // Serialize a ChannelConfigRoot with explicit Number, Enabled, and Shuffle
    // values; deserialize; compare field-by-field.
    // -----------------------------------------------------------------------

    [Fact]
    public void RoundTrip_NumberEnabledShuffle_ArePreserved()
    {
        var original = BuildRootWith(number: 42, enabled: false, shuffle: true);

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<ChannelConfigRoot>(json, JsonOptions)!;

        AssertRootEqual(original, restored);
    }

    [Fact]
    public void RoundTrip_DefaultValues_AreIncludedInOutput()
    {
        // Even when the fields have default values they must appear in the JSON
        // output (Requirement 5.5).
        var original = BuildRootWith(number: 0, enabled: true, shuffle: false);

        var json = JsonSerializer.Serialize(original);

        // Verify the JSON explicitly contains the keys.
        Assert.Contains(@"""number""", json);
        Assert.Contains(@"""enabled""", json);
        Assert.Contains(@"""shuffle""", json);
    }

    [Fact]
    public void RoundTrip_AllFieldsMatchAfterDeserialization()
    {
        var original = BuildRootWith(number: 5, enabled: true, shuffle: true);

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<ChannelConfigRoot>(json, JsonOptions)!;

        AssertRootEqual(original, restored);
    }

    /// <summary>
    /// Property 7 — iterate over a representative set of (number, enabled, shuffle)
    /// combinations and confirm every round-trip is lossless.
    /// </summary>
    [Theory]
    [InlineData(0, true, false)]
    [InlineData(0, false, false)]
    [InlineData(0, true, true)]
    [InlineData(0, false, true)]
    [InlineData(1, true, false)]
    [InlineData(1, false, true)]
    [InlineData(9999, true, false)]
    [InlineData(9999, false, true)]
    [InlineData(100, true, true)]
    [InlineData(100, false, false)]
    public void RoundTrip_ArbitraryValues_PreservedFieldByField(
        int number, bool enabled, bool shuffle)
    {
        var original = BuildRootWith(number, enabled, shuffle);

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<ChannelConfigRoot>(json, JsonOptions)!;

        AssertRootEqual(original, restored);
    }

    /// <summary>
    /// Property 7 — multiple channels in one root all round-trip correctly.
    /// </summary>
    [Fact]
    public void RoundTrip_MultipleChannels_AllFieldsPreserved()
    {
        var root = new ChannelConfigRoot
        {
            Channels = new List<ChannelConfig>
            {
                MakeChannel("ch1", number: 1, enabled: true,  shuffle: false),
                MakeChannel("ch2", number: 2, enabled: false, shuffle: true),
                MakeChannel("ch3", number: 0, enabled: true,  shuffle: true),
            }
        };

        var json = JsonSerializer.Serialize(root);
        var restored = JsonSerializer.Deserialize<ChannelConfigRoot>(json, JsonOptions)!;

        Assert.Equal(3, restored.Channels.Count);
        for (int i = 0; i < root.Channels.Count; i++)
        {
            var orig = root.Channels[i];
            var rest = restored.Channels[i];
            Assert.Equal(orig.Id, rest.Id);
            Assert.Equal(orig.Name, rest.Name);
            Assert.Equal(orig.Number, rest.Number);
            Assert.Equal(orig.Enabled, rest.Enabled);
            Assert.Equal(orig.Blocks[0].Shows![0].Shuffle,
                         rest.Blocks[0].Shows![0].Shuffle);
        }
    }

    // -----------------------------------------------------------------------
    // Additional: JSON property name contracts
    // -----------------------------------------------------------------------

    [Fact]
    public void SerializedJson_UsesLowercasePropertyNames()
    {
        var channel = MakeChannel("c1", number: 3, enabled: false, shuffle: true);
        var json = JsonSerializer.Serialize(channel);

        // The JSON keys must match what the server and Kodi addon expect.
        Assert.Contains(@"""number""", json);
        Assert.Contains(@"""enabled""", json);
        Assert.Contains(@"""shuffle""", json);
        Assert.DoesNotContain(@"""Number""", json);
        Assert.DoesNotContain(@"""Enabled""", json);
        Assert.DoesNotContain(@"""Shuffle""", json);
    }

    [Fact]
    public void StartAnchorUnix_ParsesCorrectly()
    {
        // Spot-check that StartAnchorUnix works correctly — it is computed from
        // StartAnchor and used by the schedule engine; any model test suite should
        // cover this derived property.
        var channel = new ChannelConfig { StartAnchor = "2024-01-01T00:00:00Z" };
        Assert.Equal(1704067200L, channel.StartAnchorUnix);
    }

    [Fact]
    public void StartAnchorUnix_MissingAnchor_ReturnsZero()
    {
        var channel = new ChannelConfig { StartAnchor = "" };
        Assert.Equal(0L, channel.StartAnchorUnix);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static ChannelConfigRoot BuildRootWith(int number, bool enabled, bool shuffle) =>
        new()
        {
            Channels = new List<ChannelConfig>
            {
                MakeChannel("ch1", number, enabled, shuffle)
            }
        };

    private static ChannelConfig MakeChannel(string id, int number, bool enabled, bool shuffle) =>
        new()
        {
            Id = id,
            Name = "Test Channel",
            Number = number,
            Enabled = enabled,
            StartAnchor = "2024-01-01T00:00:00Z",
            Blocks = new List<Block>
            {
                new Block
                {
                    Name = "All Day",
                    StartHour = 0,
                    EndHour = 24,
                    Offline = false,
                    Shows = new List<Show>
                    {
                        new Show
                        {
                            Name = "Show A",
                            Weight = 1,
                            Shuffle = shuffle,
                            JellyfinIds = new List<string> { "ep1", "ep2" }
                        }
                    }
                }
            }
        };

    private static void AssertRootEqual(ChannelConfigRoot expected, ChannelConfigRoot actual)
    {
        Assert.Equal(expected.Channels.Count, actual.Channels.Count);
        for (int i = 0; i < expected.Channels.Count; i++)
        {
            var exp = expected.Channels[i];
            var act = actual.Channels[i];

            Assert.Equal(exp.Id, act.Id);
            Assert.Equal(exp.Name, act.Name);
            Assert.Equal(exp.Number, act.Number);
            Assert.Equal(exp.Enabled, act.Enabled);
            Assert.Equal(exp.StartAnchor, act.StartAnchor);

            Assert.Equal(exp.Blocks.Count, act.Blocks.Count);
            for (int b = 0; b < exp.Blocks.Count; b++)
            {
                var eb = exp.Blocks[b];
                var ab = act.Blocks[b];
                Assert.Equal(eb.Name, ab.Name);
                Assert.Equal(eb.StartHour, ab.StartHour);
                Assert.Equal(eb.EndHour, ab.EndHour);
                Assert.Equal(eb.Offline, ab.Offline);

                var eShows = eb.Shows ?? new List<Show>();
                var aShows = ab.Shows ?? new List<Show>();
                Assert.Equal(eShows.Count, aShows.Count);
                for (int s = 0; s < eShows.Count; s++)
                {
                    Assert.Equal(eShows[s].Name, aShows[s].Name);
                    Assert.Equal(eShows[s].Weight, aShows[s].Weight);
                    Assert.Equal(eShows[s].Shuffle, aShows[s].Shuffle);
                    Assert.Equal(eShows[s].JellyfinIds, aShows[s].JellyfinIds);
                }
            }
        }
    }
}
