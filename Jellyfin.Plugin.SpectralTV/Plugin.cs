using System.Globalization;
using Jellyfin.Plugin.SpectralTV.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SpectralTV;

/// <summary>
/// SpectralTV Jellyfin plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasEmbeddedImage
{
    /// <summary>
    /// Embedded resource name for the SpectralTV plugin catalog icon.
    /// </summary>
    public const string PluginImageResourceName = "Jellyfin.Plugin.SpectralTV.logo.png";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public string ImageResourceName => PluginImageResourceName;

    public override string Name => "Spectral TV";

    public override Guid Id => Guid.Parse("8a3f6c2d-5b4e-4d9a-a721-3e6f8c1b2d47");

    public string DataFolder => Path.Combine(ApplicationPaths.PluginConfigurationsPath, "SpectralTV");

    public string DatabasePath => Path.Combine(DataFolder, "spectraltv.db");

    public string LogosFolder => Path.Combine(DataFolder, "logos");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var resourcePrefix = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.", GetType().Namespace);

        return
        [
            // Keep exactly one dashboard/menu entry. Channel Studio is the primary interface now.
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = Name,
                EnableInMainMenu = true,
                MenuIcon = "live_tv",
                EmbeddedResourcePath = resourcePrefix + "channelStudioPage.html"
            },
            new PluginPageInfo
            {
                Name = "SpectralTV_channelStudio.js",
                EmbeddedResourcePath = resourcePrefix + "channelStudio.js"
            },
            // The cleaned original live-channel editor remains available from Channel Studio for
            // channel creation, logos, detailed break controls, and manual Live TV URLs.
            new PluginPageInfo
            {
                Name = "SpectralTV_LiveSetup",
                DisplayName = "Spectral TV Live Setup",
                EnableInMainMenu = false,
                EmbeddedResourcePath = resourcePrefix + "configPage.html"
            },
            new PluginPageInfo
            {
                Name = "SpectralTV_admin.js",
                EmbeddedResourcePath = resourcePrefix + "admin.js"
            },
            // Explicit one-click native Live TV registration. Hidden from the dashboard menu so
            // Channel Studio remains the plugin's single top-level entry.
            new PluginPageInfo
            {
                Name = "SpectralTV_ConnectLiveTv",
                DisplayName = "Connect Spectral TV to Jellyfin",
                EnableInMainMenu = false,
                EmbeddedResourcePath = resourcePrefix + "liveTvConnectPage.html"
            },
            new PluginPageInfo
            {
                Name = "SpectralTV_livetvConnect.js",
                EmbeddedResourcePath = resourcePrefix + "liveTvConnect.js"
            }
        ];
    }
}
