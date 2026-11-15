using Jellyfin.Plugin.ClassicTV.Schedule;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ClassicTV.Tests.Schedule;

public sealed class RefreshDurationsTaskTests
{
    // ---------------------------------------------------------------------------
    // Contract tests — verify the IScheduledTask identity properties are stable.
    // These values are used by Jellyfin's task manager for display and triggering.
    // ---------------------------------------------------------------------------

    [Fact]
    public void Key_IsStable()
    {
        var task = MakeTask();
        Assert.Equal("ClassicTVRefreshDurations", task.Key);
    }

    [Fact]
    public void Name_IsNonEmpty()
    {
        var task = MakeTask();
        Assert.False(string.IsNullOrWhiteSpace(task.Name));
    }

    [Fact]
    public void Category_IsClassicTV()
    {
        var task = MakeTask();
        Assert.Equal("ClassicTV", task.Category);
    }

    [Fact]
    public void Description_IsNonEmpty()
    {
        var task = MakeTask();
        Assert.False(string.IsNullOrWhiteSpace(task.Description));
    }

    [Fact]
    public void GetDefaultTriggers_ReturnsAtLeastOneTrigger()
    {
        var task = MakeTask();
        var triggers = task.GetDefaultTriggers().ToList();
        Assert.NotEmpty(triggers);
    }

    [Fact]
    public void GetDefaultTriggers_IncludesStartupTrigger()
    {
        // DurationCache is in-memory and empty after every Jellyfin restart.
        // Without a startup trigger the schedule would use fallback durations
        // until the daily run, causing a silent schedule shift.
        var task = MakeTask();
        var triggers = task.GetDefaultTriggers().ToList();
        Assert.Contains(triggers, t => t.Type == TaskTriggerInfoType.StartupTrigger);
    }

    // ---------------------------------------------------------------------------
    // Execution safety — Plugin.Instance is null in tests (no Jellyfin host).
    // The task must handle this gracefully and not throw.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_WithNullPluginInstance_DoesNotThrow()
    {
        // Plugin.Instance is null in the test environment.
        var task = MakeTask();
        var ex = await Record.ExceptionAsync(() =>
            task.ExecuteAsync(new Progress<double>(), CancellationToken.None));
        Assert.Null(ex);
    }

    [Fact]
    public async Task ExecuteAsync_WithNullPluginInstance_LeavesDurationCacheEmpty()
    {
        var durationCache = new DurationCache();
        var task = MakeTask(durationCache);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(0, durationCache.Count);
    }    // ---------------------------------------------------------------------------
    // Cancellation — task must respect CancellationToken.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_CancelledToken_DoesNotThrow()
    {
        var task = MakeTask();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Record.ExceptionAsync(() =>
            task.ExecuteAsync(new Progress<double>(), cts.Token));

        // Should either complete cleanly or throw OperationCanceledException —
        // it must never throw anything else.
        Assert.True(ex is null or OperationCanceledException);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static RefreshDurationsTask MakeTask(DurationCache? durationCache = null) =>
        new RefreshDurationsTask(
            durationCache ?? new DurationCache(),
            NullLogger<RefreshDurationsTask>.Instance);
}
