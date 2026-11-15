using System.Text.Json;
using Jellyfin.Plugin.ClassicTV.Api;
using Jellyfin.Plugin.ClassicTV.Schedule;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ClassicTV.Tests.Schedule;

public sealed class MirrorResolverTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static MirrorSession Session(
        string user = "rachel",
        string item = "abc123",
        double position = 100,
        double duration = 1800,
        bool paused = false,
        double secondsAgo = 0,
        bool video = true,
        string title = "Show: Episode") =>
        new(user, item, title, duration, position, paused, Now.AddSeconds(-secondsAgo), video);

    [Fact]
    public void NoSessions_IsIdle()
    {
        Assert.False(MirrorResolver.Resolve(Array.Empty<MirrorSession>(), "rachel", Now).Active);
    }

    [Fact]
    public void PlayingSession_ReturnsItemAndPosition()
    {
        var r = MirrorResolver.Resolve(new[] { Session(position: 100) }, "rachel", Now);
        Assert.True(r.Active);
        Assert.Equal("abc123", r.ItemId);
        Assert.Equal(100, r.StartOffset, 3);
        Assert.Equal(1800, r.Duration);
        Assert.False(r.Paused);
    }

    [Fact]
    public void PlayingSession_PositionAdvancesSinceLastReport()
    {
        var r = MirrorResolver.Resolve(new[] { Session(position: 100, secondsAgo: 8) }, "rachel", Now);
        Assert.Equal(108, r.StartOffset, 3);
    }

    [Fact]
    public void PausedSession_PositionIsFrozenAndFlagged()
    {
        var r = MirrorResolver.Resolve(new[] { Session(position: 100, paused: true, secondsAgo: 30) }, "rachel", Now);
        Assert.True(r.Active);
        Assert.True(r.Paused);
        Assert.Equal(100, r.StartOffset, 3);
    }

    [Fact]
    public void PausedLongerThanThreshold_IsIdle()
    {
        var r = MirrorResolver.Resolve(new[] { Session(paused: true, secondsAgo: 11 * 60) }, "rachel", Now);
        Assert.False(r.Active);
    }

    [Fact]
    public void StalePlayingSession_IsIdle()
    {
        var r = MirrorResolver.Resolve(new[] { Session(secondsAgo: 120) }, "rachel", Now);
        Assert.False(r.Active);
    }

    [Fact]
    public void UserNameMatchIsCaseInsensitive()
    {
        Assert.True(MirrorResolver.Resolve(new[] { Session(user: "Rachel") }, "rachel", Now).Active);
    }

    [Fact]
    public void OtherUsersAreIgnored()
    {
        Assert.False(MirrorResolver.Resolve(new[] { Session(user: "dane") }, "rachel", Now).Active);
    }

    [Fact]
    public void NonVideoIsIgnored()
    {
        Assert.False(MirrorResolver.Resolve(new[] { Session(video: false) }, "rachel", Now).Active);
    }

    [Fact]
    public void MultipleDevices_FollowsMostRecentReport()
    {
        var sessions = new[]
        {
            Session(item: "old", secondsAgo: 40),
            Session(item: "new", secondsAgo: 2),
        };
        Assert.Equal("new", MirrorResolver.Resolve(sessions, "rachel", Now).ItemId);
    }

    [Fact]
    public void PositionIsClampedBelowDuration()
    {
        var r = MirrorResolver.Resolve(new[] { Session(position: 1795, duration: 1800, secondsAgo: 30) }, "rachel", Now);
        Assert.Equal(1799, r.StartOffset, 3);
    }

    [Fact]
    public void BlankUserName_IsIdle()
    {
        Assert.False(MirrorResolver.Resolve(new[] { Session() }, " ", Now).Active);
    }

    [Fact]
    public void ChannelConfig_RoundTripsMirrorUser()
    {
        var json = """{"channels":[{"id":"rachel","name":"Rachel","number":67,"mirrorUser":"rachel","blocks":[]}]}""";
        var root = JsonSerializer.Deserialize<ChannelConfigRoot>(json)!;
        Assert.Equal("rachel", root.Channels[0].MirrorUser);
        Assert.Contains("\"mirrorUser\":\"rachel\"", JsonSerializer.Serialize(root));
    }

    private sealed class FakeSessions : ISessionSource
    {
        public IReadOnlyList<MirrorSession> Sessions { get; set; } = Array.Empty<MirrorSession>();
        public IReadOnlyList<MirrorSession> GetSessions() => Sessions;
    }

    [Fact]
    public void Controller_WhenPluginInstanceNull_StillReturns503WithSessionSource()
    {
        var controller = new ClassicTvController(
            new ScheduleCache(), new DurationCache(),
            NullLogger<ClassicTvController>.Instance, new FakeSessions());
        var result = controller.GetChannel("rachel");
        var status = Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(result.Result);
        Assert.Equal(503, status.StatusCode);
    }
}
