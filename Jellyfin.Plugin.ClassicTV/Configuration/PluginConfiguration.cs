using System.Text.Json;
using Jellyfin.Plugin.ClassicTV.Schedule;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ClassicTV.Configuration;

/// <summary>
/// Plugin configuration stored in the Jellyfin data directory and editable
/// via the dashboard config page.
///
/// Channel definitions are stored as a raw JSON string so they can be edited
/// in a plain text field in the dashboard UI without a full CRUD interface.
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Raw JSON string containing the channel definitions.
    /// Must deserialise to <see cref="ChannelConfigRoot"/>.
    /// </summary>
    public string ChannelsJson { get; set; } = """
        {
          "channels": []
        }
        """;

    // Cached parsed result — rebuilt whenever ChannelsJson changes.
    private ChannelConfigRoot? _parsed;
    private string? _parsedFrom;

    /// <summary>
    /// The parsed channel configuration. Returns null if ChannelsJson is invalid.
    /// Re-parses only when ChannelsJson has changed since the last call.
    /// </summary>
    public ChannelConfigRoot? ParsedChannels
    {
        get
        {
            if (_parsed is not null && _parsedFrom == ChannelsJson)
                return _parsed;

            try
            {
                _parsed = JsonSerializer.Deserialize<ChannelConfigRoot>(ChannelsJson);
                _parsedFrom = ChannelsJson;
            }
            catch
            {
                _parsed = null;
            }

            return _parsed;
        }
    }
}
