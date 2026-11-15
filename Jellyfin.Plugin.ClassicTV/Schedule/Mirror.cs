using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.ClassicTV.Schedule;

/// <summary>
/// A playback session reduced to what mirroring needs. Decouples
/// <see cref="MirrorResolver"/> from Jellyfin's session types so it can be tested
/// without a Jellyfin host.
/// </summary>
public sealed record MirrorSession(
    string UserName,
    string ItemId,
    string Title,
    double DurationSeconds,
    double PositionSeconds,
    bool IsPaused,
    DateTime LastUpdateUtc,
    bool IsVideo);

/// <summary>Source of the server's current playback sessions.</summary>
public interface ISessionSource
{
    IReadOnlyList<MirrorSession> GetSessions();
}

/// <summary>What a mirror channel should be playing right now.</summary>
public sealed record MirrorResult(
    bool Active,
    string ItemId = "",
    string Title = "",
    double StartOffset = 0,
    double Duration = 0,
    bool Paused = false)
{
    public static MirrorResult Idle { get; } = new(false);
}

/// <summary>
/// Works out what the mirrored user is watching and how far into it they are
/// at the moment of the request.
/// </summary>
public static class MirrorResolver
{
    /// <summary>
    /// A playing session that has not checked in for this long is treated as gone
    /// (client crashed or lost network without reporting a stop).
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(90);

    /// <summary>A session paused for this long is treated as abandoned.</summary>
    public static readonly TimeSpan PausedAbandonedAfter = TimeSpan.FromMinutes(10);

    public static MirrorResult Resolve(IEnumerable<MirrorSession> sessions, string userName, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(userName))
            return MirrorResult.Idle;

        // Newest first: if the user has several devices playing, follow the one
        // that reported most recently.
        var session = sessions
            .Where(s => s.IsVideo
                        && !string.IsNullOrEmpty(s.ItemId)
                        && string.Equals(s.UserName, userName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.LastUpdateUtc)
            .FirstOrDefault();

        if (session is null)
            return MirrorResult.Idle;

        var age = nowUtc - session.LastUpdateUtc;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        if (session.IsPaused)
        {
            if (age > PausedAbandonedAfter)
                return MirrorResult.Idle;
        }
        else if (age > StaleAfter)
        {
            return MirrorResult.Idle;
        }

        // Position was true at the last report; playing sessions advance in real time since.
        double position = session.PositionSeconds + (session.IsPaused ? 0 : age.TotalSeconds);
        if (session.DurationSeconds > 1)
            position = Math.Min(position, session.DurationSeconds - 1);
        position = Math.Max(position, 0);

        return new MirrorResult(
            Active: true,
            ItemId: session.ItemId,
            Title: session.Title,
            StartOffset: position,
            Duration: session.DurationSeconds,
            Paused: session.IsPaused);
    }
}

/// <summary>Reads sessions from Jellyfin's <see cref="ISessionManager"/>.</summary>
public sealed class JellyfinSessionSource : ISessionSource
{
    private readonly ISessionManager _sessions;

    public JellyfinSessionSource(ISessionManager sessions) => _sessions = sessions;

    public IReadOnlyList<MirrorSession> GetSessions()
    {
        var list = new List<MirrorSession>();
        foreach (var s in _sessions.Sessions)
        {
            var item = s.NowPlayingItem;
            if (item is null || string.IsNullOrEmpty(s.UserName))
                continue;

            double duration = (item.RunTimeTicks ?? 0) / (double)TimeSpan.TicksPerSecond;
            double position = (s.PlayState?.PositionTicks ?? 0) / (double)TimeSpan.TicksPerSecond;
            var lastUpdate = s.LastPlaybackCheckIn > s.LastActivityDate
                ? s.LastPlaybackCheckIn
                : s.LastActivityDate;

            string title = string.IsNullOrEmpty(item.SeriesName)
                ? item.Name ?? string.Empty
                : $"{item.SeriesName}: {item.Name}";

            list.Add(new MirrorSession(
                UserName: s.UserName,
                ItemId: item.Id.ToString("N"),
                Title: title,
                DurationSeconds: duration,
                PositionSeconds: position,
                IsPaused: s.PlayState?.IsPaused ?? false,
                LastUpdateUtc: DateTime.SpecifyKind(lastUpdate, DateTimeKind.Utc),
                IsVideo: item.MediaType == MediaType.Video));
        }

        return list;
    }
}
