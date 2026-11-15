using Jellyfin.Plugin.ClassicTV.Schedule;
using Xunit;

namespace Jellyfin.Plugin.ClassicTV.Tests.Schedule;

public sealed class ScheduleCacheTests
{
    [Fact]
    public void Get_AfterSet_ReturnsSameResult()
    {
        var cache = new ScheduleCache();
        var result = new ScheduleResult { JellyfinId = "abc", Title = "Test", StartOffset = 42.0, Duration = 1800.0 };

        cache.Set("ch1", 1000, result);

        var retrieved = cache.Get("ch1", 1000);
        Assert.NotNull(retrieved);
        Assert.Equal("abc", retrieved.JellyfinId);
        Assert.Equal(42.0, retrieved.StartOffset);
    }

    [Fact]
    public void Get_SameMinuteDifferentSecond_ReturnsCachedResult()
    {
        var cache = new ScheduleCache();
        var result = new ScheduleResult { JellyfinId = "abc", Title = "Test", Duration = 1800.0 };

        // Store at the start of minute 16666 (second 999960)
        long baseSecond = 16666L * 60; // 999960
        cache.Set("ch1", baseSecond, result);

        // Retrieve at :45 seconds of the same minute — same key
        var retrieved = cache.Get("ch1", baseSecond + 45);
        Assert.NotNull(retrieved);
    }

    [Fact]
    public void Get_CacheMiss_ReturnsNull()
    {
        var cache = new ScheduleCache();
        Assert.Null(cache.Get("ch1", 99999));
    }

    [Fact]
    public void Get_DifferentChannel_ReturnsDifferentResult()
    {
        var cache = new ScheduleCache();
        var r1 = new ScheduleResult { JellyfinId = "item1", Duration = 1800.0 };
        var r2 = new ScheduleResult { JellyfinId = "item2", Duration = 1800.0 };

        cache.Set("ch1", 1000, r1);
        cache.Set("ch2", 1000, r2);

        Assert.Equal("item1", cache.Get("ch1", 1000)!.JellyfinId);
        Assert.Equal("item2", cache.Get("ch2", 1000)!.JellyfinId);
    }

    [Fact]
    public void EvictBefore_RemovesOldEntries()
    {
        var cache = new ScheduleCache();
        cache.Set("ch1", 500 * 60, new ScheduleResult { JellyfinId = "old", Duration = 1800.0 });
        cache.Set("ch1", 1000 * 60, new ScheduleResult { JellyfinId = "new", Duration = 1800.0 });

        cache.EvictBefore(600 * 60); // evict anything before minute 600

        Assert.Null(cache.Get("ch1", 500 * 60));
        Assert.NotNull(cache.Get("ch1", 1000 * 60));
    }

    [Fact]
    public void Count_ReflectsStoredEntries()
    {
        var cache = new ScheduleCache();
        Assert.Equal(0, cache.Count);

        cache.Set("ch1", 1000, new ScheduleResult { Duration = 1800.0 });
        cache.Set("ch2", 1000, new ScheduleResult { Duration = 1800.0 });
        Assert.Equal(2, cache.Count);
    }
}
