using Jellyfin.Plugin.ClassicTV.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ClassicTV;

/// <summary>
/// Classic TV Jellyfin plugin entry point.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Singleton access used by the cache worker to read configuration.</summary>
    public static Plugin? Instance { get; private set; }

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "ClassicTV";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    /// <inheritdoc />
    public override string Description => "Classic TV channel scheduling — serves what should be playing now and at what offset.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "ClassicTV",
            MenuSection = "server",
            MenuIcon = "tv",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        };

        // Page controller. The React dashboard does not execute inline <script>
        // in injected config pages, so the page logic is a separate ES module
        // referenced by data-controller="__plugin/classictvjs" in configPage.html.
        yield return new PluginPageInfo
        {
            Name = "classictvjs",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.classictv.js"
        };
    }
}
