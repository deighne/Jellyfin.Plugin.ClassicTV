using Jellyfin.Plugin.ClassicTV.Schedule;
using Xunit;

namespace Jellyfin.Plugin.ClassicTV.Tests.Schedule;

public sealed class GuideResolverTests : IDisposable
{
    private readonly string _tempDir;

    public GuideResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"classictv-guide-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors in temp directory.
        }
    }

    [Theory]
    [InlineData("guide-1794182400", "guide-1794182400")]
    [InlineData("guide-1794182400.mp4", "guide-1794182400")]
    [InlineData("guide-0.MP4", "guide-0")]
    public void NormalizeChunkId_ValidIds_ReturnsNormalized(string raw, string expected)
    {
        Assert.Equal(expected, GuideResolver.NormalizeChunkId(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../guide-1794182400")]
    [InlineData("guide-1794182400/../../etc/passwd")]
    [InlineData("guide-abc")]
    [InlineData("other-1794182400")]
    public void NormalizeChunkId_InvalidOrTraversalIds_ReturnsNull(string? raw)
    {
        Assert.Null(GuideResolver.NormalizeChunkId(raw));
    }

    [Fact]
    public void ResolveSlot_ExactChunkExists_ReturnsExactItemAndOffset()
    {
        long chunkStart = 1794182400L; // Exact multiple of 900
        long now = chunkStart + 450L;
        File.WriteAllBytes(Path.Combine(_tempDir, $"guide-{chunkStart}.mp4"), new byte[] { 1, 2, 3 });

        var res = GuideResolver.ResolveSlot("TV Guide", now, _tempDir);

        Assert.True(res.Active);
        Assert.False(res.IsFallback);
        Assert.Equal($"guide-{chunkStart}", res.ItemId);
        Assert.Equal("TV Guide", res.Title);
        Assert.Equal(450.0, res.StartOffset);
        Assert.Equal(900.0, res.Duration);
        Assert.Equal($"guide-{chunkStart + 900}", res.NextItemId);
    }

    [Fact]
    public void ResolveSlot_ExactChunkMissing_FallsBackToNewestChunk()
    {
        long olderChunk = 1794180600L;
        long newerChunk = 1794181500L;
        long currentChunk = 1794182400L;
        long now = currentChunk + 123L;

        File.WriteAllBytes(Path.Combine(_tempDir, $"guide-{olderChunk}.mp4"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_tempDir, $"guide-{newerChunk}.mp4"), new byte[] { 2 });

        var res = GuideResolver.ResolveSlot("TV Guide", now, _tempDir);

        Assert.True(res.Active);
        Assert.True(res.IsFallback);
        Assert.Equal($"guide-{newerChunk}", res.ItemId);
        Assert.Equal(123.0, res.StartOffset);
        Assert.Equal(900.0, res.Duration);
        Assert.Equal($"guide-{currentChunk + 900}", res.NextItemId);
    }

    [Fact]
    public void ResolveSlot_EmptyDirectory_ReturnsOffline()
    {
        var res = GuideResolver.ResolveSlot("TV Guide", 1794182500L, _tempDir);
        Assert.False(res.Active);
    }

    [Fact]
    public void ResolveStreamFilePath_ExactAndFallbackAndTraversal()
    {
        long chunk1 = 1794181500L;
        string path1 = Path.Combine(_tempDir, $"guide-{chunk1}.mp4");
        File.WriteAllBytes(path1, new byte[] { 0x00 });

        // Exact match (with and without .mp4)
        Assert.Equal(path1, GuideResolver.ResolveStreamFilePath($"guide-{chunk1}", _tempDir));
        Assert.Equal(path1, GuideResolver.ResolveStreamFilePath($"guide-{chunk1}.mp4", _tempDir));

        // Fallback when next chunk is requested before render finishes
        Assert.Equal(path1, GuideResolver.ResolveStreamFilePath("guide-1794182400", _tempDir));

        // Path traversal rejected
        Assert.Null(GuideResolver.ResolveStreamFilePath("../guide-1794181500", _tempDir));
    }
}
