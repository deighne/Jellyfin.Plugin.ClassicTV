using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ClassicTV.Api;
using Jellyfin.Plugin.ClassicTV.Schedule;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Controller.Sorting;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ClassicTV.Tests.Api;

/// <summary>
/// Unit tests for <see cref="ManagementController"/>.
///
/// Plugin.Instance is always null in the test environment (no Jellyfin host).
/// Tests focus on:
///   - 503 response shape when Plugin.Instance is null (GET and POST channels)
///   - 404 for non-GUID seriesId (GUID parse happens before any library call)
///   - Documentation of ASP.NET model-binding 400 behaviour for invalid JSON
///   - 200 with empty list when library returns no series
/// </summary>
public sealed class ManagementControllerTests
{
    // -------------------------------------------------------------------------
    // Factory helper
    // -------------------------------------------------------------------------

    private static ManagementController MakeController(
        ILibraryManager? libraryManager = null,
        ITaskManager? taskManager = null)
    {
        var cache = new ScheduleCache();
        var durations = new DurationCache();
        var worker = new ScheduleCacheWorker(
            cache,
            durations,
            NullLogger<ScheduleCacheWorker>.Instance);

        return new ManagementController(
            worker,
            libraryManager ?? StubLibraryManagerProxy.Create(),
            taskManager ?? new StubTaskManager(),
            NullLogger<ManagementController>.Instance);
    }

    // -------------------------------------------------------------------------
    // GET /ClassicTV/manage/channels — returns 503 when Plugin.Instance == null
    // -------------------------------------------------------------------------

    [Fact]
    public void GetManageChannels_WhenPluginInstanceNull_Returns503()
    {
        // Plugin.Instance is always null in tests — no Jellyfin host is present.
        var controller = MakeController();

        var result = controller.GetManageChannels();

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, status.StatusCode);
    }

    [Fact]
    public void GetManageChannels_WhenPluginInstanceNull_BodyIsNotNull()
    {
        // The 503 should carry a human-readable message body, not null,
        // so callers can log the reason.
        var controller = MakeController();

        var result = controller.GetManageChannels();

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, status.StatusCode);
        Assert.NotNull(status.Value);
    }

    [Fact]
    public void GetManageChannels_WhenPluginInstanceNull_DoesNotThrow()
    {
        var controller = MakeController();

        var ex = Record.Exception(() => controller.GetManageChannels());

        Assert.Null(ex);
    }

    // -------------------------------------------------------------------------
    // POST /ClassicTV/manage/channels — returns 503 when Plugin.Instance == null
    //
    // ASP.NET model binding rejects undeserializable / null JSON bodies with
    // HTTP 400 before the controller method is ever called. The controller itself
    // never sees a null body when [FromBody] + [ApiController] are applied.
    //
    // What we CAN test without a live host: when a valid ChannelConfigRoot body
    // reaches the method but Plugin.Instance is null, the controller must return
    // 503 (not throw, not return 204).
    //
    // Property 13 note: "POST with invalid JSON always returns 400" is enforced
    // by ASP.NET model binding infrastructure (not by controller code) and is
    // verified by integration tests, not by this unit test.
    // -------------------------------------------------------------------------

    [Fact]
    public void PostManageChannels_WhenPluginInstanceNull_Returns503()
    {
        var controller = MakeController();
        var body = new ChannelConfigRoot { Channels = new List<ChannelConfig>() };

        var result = controller.PostManageChannels(body);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, status.StatusCode);
    }

    [Fact]
    public void PostManageChannels_WhenPluginInstanceNull_DoesNotThrow()
    {
        var controller = MakeController();
        var body = new ChannelConfigRoot { Channels = new List<ChannelConfig>() };

        var ex = Record.Exception(() => controller.PostManageChannels(body));

        Assert.Null(ex);
    }

    [Fact]
    public void PostManageChannels_WhenPluginInstanceNull_BodyIsNotNull()
    {
        var controller = MakeController();
        var body = new ChannelConfigRoot { Channels = new List<ChannelConfig>() };

        var result = controller.PostManageChannels(body);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, status.StatusCode);
        Assert.NotNull(status.Value);
    }

    // -------------------------------------------------------------------------
    // GET /ClassicTV/manage/library/series/{seriesId}/episodes
    // Returns 404 for a non-GUID seriesId.
    //
    // The Guid.TryParse check is the very first operation in GetEpisodes, before
    // any call to ILibraryManager. The test never exercises the stub library and
    // does not require Plugin.Instance to be non-null.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12345")]
    [InlineData("xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx")]       // looks like GUID format but bad hex
    [InlineData("00000000-0000-0000-0000-00000000000g")]       // 'g' is not valid hex
    public void GetEpisodes_WithNonGuidSeriesId_Returns404(string invalidId)
    {
        var controller = MakeController();

        var result = controller.GetEpisodes(invalidId);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal(404, notFound.StatusCode);
    }

    [Fact]
    public void GetEpisodes_WithNonGuidSeriesId_Returns404WithMessage()
    {
        var controller = MakeController();

        var result = controller.GetEpisodes("not-a-guid");

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal(404, notFound.StatusCode);
        Assert.NotNull(notFound.Value);
    }

    // -------------------------------------------------------------------------
    // GET /ClassicTV/manage/library/series/{seriesId}/episodes
    // Returns 404 for a valid GUID that doesn't resolve to a series in the library.
    //
    // StubLibraryManager.GetItemById returns null, which triggers the "not found"
    // branch — identical to a real library miss for an unknown ID.
    // -------------------------------------------------------------------------

    [Fact]
    public void GetEpisodes_WithValidGuidButNullLibraryResult_Returns404()
    {
        var controller = MakeController(); // StubLibraryManager.GetItemById → null
        var validGuid = Guid.NewGuid().ToString();

        var result = controller.GetEpisodes(validGuid);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal(404, notFound.StatusCode);
    }

    // -------------------------------------------------------------------------
    // GET /ClassicTV/manage/library/series
    // Returns 200 with an empty series list when the library is empty.
    //
    // GetSeries does not short-circuit on null Plugin.Instance — it queries
    // ILibraryManager directly. The stub returns an empty result set, so the
    // expected response is HTTP 200 with an empty Series array.
    // -------------------------------------------------------------------------

    [Fact]
    public void GetSeries_WithEmptyLibrary_Returns200WithEmptyList()
    {
        var controller = MakeController(); // StubLibraryManager.GetItemsResult → empty

        var result = controller.GetSeries();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, ok.StatusCode);
        var response = Assert.IsType<SeriesListResponse>(ok.Value);
        Assert.Empty(response.Series);
    }
}

// =============================================================================
// Minimal stubs — no mocking library is available in this test project.
// Only the three methods called by ManagementController are given real
// implementations; all other interface members throw NotImplementedException
// so that any unexpected call fails loudly rather than silently.
// =============================================================================

/// <summary>
/// Stub <see cref="ILibraryManager"/> that returns empty/null results for the
/// methods used by <see cref="ManagementController"/> using <see cref="DispatchProxy"/>
/// so new upstream <see cref="ILibraryManager"/> methods do not break compilation.
/// </summary>
public class StubLibraryManagerProxy : DispatchProxy
{
    public static ILibraryManager Create() =>
        DispatchProxy.Create<ILibraryManager, StubLibraryManagerProxy>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        return targetMethod?.Name switch
        {
            nameof(ILibraryManager.GetItemsResult) => new QueryResult<BaseItem>(Array.Empty<BaseItem>()),
            nameof(ILibraryManager.GetItemList) => new List<BaseItem>(),
            nameof(ILibraryManager.GetCount) => 0,
            nameof(ILibraryManager.GetItemById) => null,
            _ => throw new NotImplementedException($"Method {targetMethod?.Name} is not implemented on StubLibraryManagerProxy")
        };
    }
}

/// <summary>
/// Stub <see cref="ITaskManager"/> that no-ops <c>QueueScheduledTask&lt;T&gt;()</c>
/// and implements <see cref="IDisposable"/>. All other members throw.
/// </summary>
file sealed class StubTaskManager : ITaskManager, IDisposable
{
    public IReadOnlyList<IScheduledTaskWorker> ScheduledTasks =>
        Array.Empty<IScheduledTaskWorker>();

#pragma warning disable CS0067
    public event EventHandler<Jellyfin.Data.Events.GenericEventArgs<IScheduledTaskWorker>>? TaskExecuting;
    public event EventHandler<TaskCompletionEventArgs>? TaskCompleted;
#pragma warning restore CS0067

    // Methods called by ManagementController:
    public void QueueScheduledTask<T>() where T : IScheduledTask { }
    public void QueueScheduledTask<T>(TaskOptions options) where T : IScheduledTask { }
    public void QueueScheduledTask(IScheduledTask task, TaskOptions options) { }

    // Unused but required:
    public void AddTasks(IEnumerable<IScheduledTask> tasks) => throw new NotImplementedException();
    public void Cancel(IScheduledTaskWorker task) => throw new NotImplementedException();
    public void CancelIfRunning<T>() where T : IScheduledTask => throw new NotImplementedException();
    public void CancelIfRunningAndQueue<T>() where T : IScheduledTask => throw new NotImplementedException();
    public void CancelIfRunningAndQueue<T>(TaskOptions options) where T : IScheduledTask => throw new NotImplementedException();
    public Task Execute(IScheduledTaskWorker task, TaskOptions options) => throw new NotImplementedException();
    public void Execute<T>() where T : IScheduledTask => throw new NotImplementedException();
    public void QueueIfNotRunning<T>() where T : IScheduledTask => throw new NotImplementedException();
    public void Dispose() { }
}
