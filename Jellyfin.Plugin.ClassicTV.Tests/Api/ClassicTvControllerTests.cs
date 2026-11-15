using Jellyfin.Plugin.ClassicTV.Api;
using Jellyfin.Plugin.ClassicTV.Schedule;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.ClassicTV.Tests.Api;

/// <summary>
/// Tests for GET /ClassicTV/channels.
///
/// Plugin.Instance is null in tests (no Jellyfin host), so the endpoint
/// returns 503. These tests verify the response shape and graceful
/// degradation — not the full data path which requires a live Jellyfin host.
/// </summary>
public sealed class ClassicTvControllerTests
{
    private static ClassicTvController MakeController(string? guideCacheDir = null) =>
        new ClassicTvController(
            new ScheduleCache(),
            new DurationCache(),
            NullLogger<ClassicTvController>.Instance,
            guideCacheDir: guideCacheDir);

    // ---------------------------------------------------------------------------
    // GET /ClassicTV/channels — channel list endpoint
    // ---------------------------------------------------------------------------

    [Fact]
    public void GetChannels_WhenPluginInstanceNull_Returns503()
    {
        // Plugin.Instance is always null in the test environment.
        var controller = MakeController();
        var result = controller.GetChannels();
        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, status.StatusCode);
    }

    [Fact]
    public void GetChannels_ReturnsActionResultOfChannelListResponse()
    {
        // Verify the return type is correct (structural test — no Jellyfin host needed).
        var controller = MakeController();
        var result = controller.GetChannels();
        Assert.NotNull(result);
    }

    // ---------------------------------------------------------------------------
    // GET /ClassicTV/channel/{channelId} — existing endpoint regression
    // ---------------------------------------------------------------------------

    [Fact]
    public void GetChannel_WhenPluginInstanceNull_Returns503()
    {
        var controller = MakeController();
        var result = controller.GetChannel("sitcoms");
        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, status.StatusCode);
    }

    // ---------------------------------------------------------------------------
    // GET /ClassicTV/guide/stream/{chunkId}
    // ---------------------------------------------------------------------------

    [Fact]
    public void GetGuideStream_InvalidChunkId_Returns400()
    {
        var controller = MakeController();
        var result = controller.GetGuideStream("../etc/passwd");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetGuideStream_MissingChunkInEmptyDir_Returns404()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"classictv-ctrl-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var controller = MakeController(tempDir);
            var result = controller.GetGuideStream("guide-1794182400");
            Assert.IsType<NotFoundObjectResult>(result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetGuideStream_ValidChunk_ReturnsPhysicalFileWithRangeProcessing()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"classictv-ctrl-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string expectedPath = Path.Combine(tempDir, "guide-1794182400.mp4");
            File.WriteAllBytes(expectedPath, new byte[] { 0x00, 0x01 });

            var controller = MakeController(tempDir);
            var result = controller.GetGuideStream("guide-1794182400");
            var fileResult = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal(expectedPath, fileResult.FileName);
            Assert.Equal("video/mp4", fileResult.ContentType);
            Assert.True(fileResult.EnableRangeProcessing);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
