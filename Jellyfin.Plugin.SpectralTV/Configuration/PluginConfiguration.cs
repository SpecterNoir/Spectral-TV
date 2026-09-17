using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SpectralTV.Configuration;

/// <summary>Small set of server-wide options used by the current Spectral TV product.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public string? PublicBaseUrl { get; set; }

    public int PlayoutDaysToBuild { get; set; } = 3;

    public string ScheduleTimeZone { get; set; } = string.Empty;

    public bool DebugLogging { get; set; }

    /// <summary>
    /// Jellyfin Live TV tuner created or adopted by the explicit Spectral TV connection workflow.
    /// Keeping the id lets us update only our own tuner when the server address changes.
    /// </summary>
    public string? LiveTvTunerHostId { get; set; }

    /// <summary>
    /// Jellyfin XMLTV provider created or adopted by the explicit Spectral TV connection workflow.
    /// </summary>
    public string? LiveTvListingsProviderId { get; set; }

    /// <summary>
    /// Per-user Jellyfin Home slot reserved for the Spectral Channels row. Jellyfin only accepts
    /// built-in homesection values, so this mapping must live outside its native preference fields.
    /// </summary>
    public List<ChannelsHomeSectionPreference> ChannelsHomeSections { get; set; } = [];
}

public sealed class ChannelsHomeSectionPreference
{
    public string UserId { get; set; } = string.Empty;

    public int SectionIndex { get; set; }
}
