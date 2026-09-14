using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SpectralTV.Configuration;

/// <summary>Small set of server-wide options used by the current Spectral TV product.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public string? PublicBaseUrl { get; set; }

    public int PlayoutDaysToBuild { get; set; } = 3;

    public string ScheduleTimeZone { get; set; } = string.Empty;

    public bool DebugLogging { get; set; }
}
