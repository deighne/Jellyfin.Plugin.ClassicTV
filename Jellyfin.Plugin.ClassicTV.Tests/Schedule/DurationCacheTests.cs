using Jellyfin.Plugin.ClassicTV.Schedule;
using Xunit;

namespace Jellyfin.Plugin.ClassicTV.Tests.Schedule;

public sealed class DurationCacheTests
{
    [Fact]
    public void GetOrFallback_UnknownId_ReturnsFallback()
    {
        var cache = new DurationCache();
        Assert.Equal(ScheduleEngine.FallbackDurationSeconds, cache.GetOrFallback("unknown-id"));
    }

    [Fact]
    public void GetOrFallback_KnownId_ReturnsStoredValue()
    {
        var cache = new DurationCache();
        cache.Set("abc123", 2700.0);
        Assert.Equal(2700.0, cache.GetOrFallback("abc123"));
    }

    [Fact]
    public void Set_OverwritesExistingValue()
    {
        var cache = new DurationCache();
        cache.Set("abc123", 1800.0);
        cache.Set("abc123", 2700.0);
        Assert.Equal(2700.0, cache.GetOrFallback("abc123"));
    }

    [Fact]
    public void AsReadOnlyDictionary_ReflectsCurrentState()
    {
        var cache = new DurationCache();
        cache.Set("id1", 1800.0);
        cache.Set("id2", 2700.0);

        var dict = cache.AsReadOnlyDictionary();
        Assert.Equal(2, dict.Count);
        Assert.Equal(1800.0, dict["id1"]);
        Assert.Equal(2700.0, dict["id2"]);
    }

    [Fact]
    public void AsReadOnlyDictionary_AfterUpdate_ReflectsNewValue()
    {
        var cache = new DurationCache();
        cache.Set("id1", 1800.0);
        cache.Set("id1", 2700.0);

        var dict = cache.AsReadOnlyDictionary();
        Assert.Equal(2700.0, dict["id1"]);
    }

    [Fact]
    public void Count_ReflectsNumberOfEntries()
    {
        var cache = new DurationCache();
        Assert.Equal(0, cache.Count);
        cache.Set("a", 1800.0);
        cache.Set("b", 2700.0);
        Assert.Equal(2, cache.Count);
    }
}
