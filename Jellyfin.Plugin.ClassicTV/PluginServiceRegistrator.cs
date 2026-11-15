using Jellyfin.Plugin.ClassicTV.Schedule;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ClassicTV;

/// <summary>
/// Registers plugin services into the Jellyfin DI container at startup.
///
/// IScheduledTask (RefreshDurationsTask) is NOT registered here — Jellyfin
/// discovers IScheduledTask implementations automatically via assembly scanning.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // DurationCache: populated by RefreshDurationsTask after library is ready.
        serviceCollection.AddSingleton<DurationCache>();

        // ScheduleCache: pre-computed schedule slots, shared with the controller.
        serviceCollection.AddSingleton<ScheduleCache>();

        // Mirror channels read the mirrored user's playback session.
        serviceCollection.AddSingleton<ISessionSource, JellyfinSessionSource>();

        // Register ScheduleCacheWorker as a singleton so it can be injected into
        // RefreshDurationsTask. AddHostedService alone doesn't make it resolvable
        // by concrete type — we need an explicit singleton registration first.
        serviceCollection.AddSingleton<ScheduleCacheWorker>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<ScheduleCacheWorker>());
    }
}
