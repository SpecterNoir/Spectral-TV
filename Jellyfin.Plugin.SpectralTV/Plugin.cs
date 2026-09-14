using System.Globalization;
using System.Reflection;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the active plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public string ImageResourceName => PluginImageResourceName;

    /// <inheritdoc />
    public override string Name => "Spectral TV";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("8a3f6c2d-5b4e-4d9a-a721-3e6f8c1b2d47");

    /// <summary>
    /// Gets the plugin data folder path.
    /// </summary>
    public string DataFolder => Path.Combine(ApplicationPaths.PluginConfigurationsPath, "SpectralTV");

    /// <summary>
    /// Gets the SQLite database file path.
    /// </summary>
    public string DatabasePath => Path.Combine(DataFolder, "spectraltv.db");

    /// <summary>
    /// Gets the cached logo storage folder.
    /// </summary>
    public string LogosFolder => Path.Combine(DataFolder, "logos");

    /// <summary>
    /// Gets the Emergency Broadcast System asset folder.
    /// </summary>
    public string EbsFolder => Path.Combine(DataFolder, "ebs");

    /// <summary>
    /// Gets the folder for user-uploaded EBS slate images.
    /// </summary>
    public string EbsCustomSlatesFolder => Path.Combine(EbsFolder, "custom");

    /// <summary>
    /// Gets logos shipped inside the plugin install folder.
    /// </summary>
    public string BundledLogosFolder => Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ApplicationPaths.PluginsPath,
        "logos",
        "binarygeek119");

    /// <summary>
    /// Gets the WeatherStar asset folder.
    /// </summary>
    public string WeatherStarFolder => Path.Combine(DataFolder, "weatherstar");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var resourcePrefix = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.", GetType().Namespace);

        return
        [
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = Name,
                EnableInMainMenu = true,
                EmbeddedResourcePath = resourcePrefix + "configPage.html"
            },
            new PluginPageInfo
            {
                Name = "SpectralTV_Programming",
                DisplayName = "Spectral TV Programming",
                EnableInMainMenu = true,
                EmbeddedResourcePath = resourcePrefix + "programmingPage.html"
            },
            new PluginPageInfo
            {
                Name = "SpectralTV_admin.css",
                EmbeddedResourcePath = resourcePrefix + "admin.css"
            },
            new PluginPageInfo
            {
                Name = "SpectralTV_admin.js",
                EmbeddedResourcePath = resourcePrefix + "admin.js"
            },
            new PluginPageInfo
            {
                Name = "SpectralTV_programming.js",
                EmbeddedResourcePath = resourcePrefix + "programming.js"
            }
        ];
    }
}
