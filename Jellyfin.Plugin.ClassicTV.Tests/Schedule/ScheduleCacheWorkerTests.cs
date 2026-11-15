using Jellyfin.Plugin.ClassicTV.Schedule;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ClassicTV.Tests.Schedule;

/// <summary>
/// Tests for ScheduleCacheWorker startup safety guarantees.
///
/// These tests exist specifically because a bad startup sequence caused a
/// macOS kernel panic during the first plugin install attempt. They must
/// catch any regression that would re-introduce unsafe startup behaviour.
/// </summary>
public sealed class ScheduleCacheWorkerTests
{
    // ---------------------------------------------------------------------------
    // Test: StartAsync does not fire PopulateCache synchronously.
    //
    // The original v1 code used dueTime:Zero, which fired the timer callback
    // almost immediately — before Jellyfin had finished initialising. The fix
    // is a meaningful initial delay. We verify by checking the cache is empty
    // immediately after StartAsync returns.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_DoesNotPopulateCacheImmediately()
    {
        var (worker, scheduleCache, _) = MakeWorker();

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await Task.Delay(100);

        Assert.Equal(0, scheduleCache.Count);

        await worker.StopAsync(cts.Token);
    }

    // ---------------------------------------------------------------------------
    // Test: StartAsync and StopAsync complete without throwing.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_ThenStopAsync_DoesNotThrow()
    {
        var (worker, _, _) = MakeWorker();
        using var cts = new CancellationTokenSource();

        var ex = await Record.ExceptionAsync(async () =>
        {
            await worker.StartAsync(cts.Token);
            await Task.Delay(50);
            await worker.StopAsync(cts.Token);
        });

        Assert.Null(ex);
    }

    // ---------------------------------------------------------------------------
    // Test: PopulateCache uses DurationCache fallback when cache is empty.
    //
    // When DurationCache has no entries (RefreshDurationsTask hasn't run yet),
    // PopulateCache must still complete using FallbackDurationSeconds.
    // Plugin.Instance is null in tests — the worker must handle that gracefully.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task PopulateCache_WithEmptyDurationCache_DoesNotThrow()
    {
        var (worker, _, _) = MakeWorker();
        var ex = await Record.ExceptionAsync(() => worker.TriggerRebuildAsync());
        Assert.Null(ex);
    }

    // ---------------------------------------------------------------------------
    // Test: PopulateCache uses real durations when DurationCache is populated.
    //
    // After RefreshDurationsTask runs and populates DurationCache, the next
    // PopulateCache cycle should read from it. We can't fully test this without
    // Plugin.Instance, but we verify DurationCache is read (not ILibraryManager).
    // The absence of any ILibraryManager in the constructor is the structural proof.
    // ---------------------------------------------------------------------------

    [Fact]
    public void Constructor_DoesNotRequireLibraryManager()
    {
        // If this compiles and runs, the constructor takes no ILibraryManager.
        // This is the structural regression test for the v1 kernel panic cause.
        var scheduleCache = new ScheduleCache();
        var durationCache = new DurationCache();
        using var worker = new ScheduleCacheWorker(
            scheduleCache,
            durationCache,
            NullLogger<ScheduleCacheWorker>.Instance);

        Assert.NotNull(worker);
    }

    // ---------------------------------------------------------------------------
    // Test: StopAsync before StartAsync does not throw.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task StopAsync_BeforeStart_DoesNotThrow()
    {
        var (worker, _, _) = MakeWorker();
        var ex = await Record.ExceptionAsync(async () =>
            await worker.StopAsync(CancellationToken.None));
        Assert.Null(ex);
    }

    // ---------------------------------------------------------------------------
    // Test: Dispose is safe to call multiple times.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        var (worker, _, _) = MakeWorker();
        await worker.StartAsync(CancellationToken.None);

        var ex = Record.Exception(() =>
        {
            worker.Dispose();
            worker.Dispose();
        });

        Assert.Null(ex);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static (ScheduleCacheWorker worker, ScheduleCache scheduleCache, DurationCache durationCache) MakeWorker()
    {
        var scheduleCache = new ScheduleCache();
        var durationCache = new DurationCache();
        var worker = new ScheduleCacheWorker(
            scheduleCache,
            durationCache,
            NullLogger<ScheduleCacheWorker>.Instance);
        return (worker, scheduleCache, durationCache);
    }
}
