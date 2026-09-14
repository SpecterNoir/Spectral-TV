using Jellyfin.Plugin.SpectralTV.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SpectralTV.Api;

/// <summary>Setup helpers for Jellyfin Live TV integration.</summary>
[ApiController]
[Route("SpectralTV/api/setup")]
public class SetupController : ControllerBase
{
    private const string SpectralTunerFriendlyName = "Spectral TV";
    private const string M3uTunerType = "m3u";
    private const string XmlTvProviderType = "xmltv";

    private readonly IServerApplicationHost _appHost;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly ITunerHostManager _tunerHostManager;
    private readonly IListingsManager _listingsManager;

    public SetupController(
        IServerApplicationHost appHost,
        IServerConfigurationManager configurationManager,
        ITunerHostManager tunerHostManager,
        IListingsManager listingsManager)
    {
        _appHost = appHost;
        _configurationManager = configurationManager;
        _tunerHostManager = tunerHostManager;
        _listingsManager = listingsManager;
    }

    [HttpGet("urls")]
    [AllowAnonymous]
    public ActionResult<object> GetUrls()
    {
        try
        {
            return Ok(BuildUrlResponse());
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Could not build setup URLs: {ex.Message}" });
        }
    }

    [HttpGet("settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> GetSettings()
    {
        return Ok(new
        {
            publicBaseUrl = Plugin.Instance?.Configuration.PublicBaseUrl ?? string.Empty,
            playoutDaysToBuild = PlayoutScheduleHelper.GetPlayoutDaysToBuild()
        });
    }

    [HttpPut("settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> UpdateSettings([FromBody] SetupSettingsRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        plugin.Configuration.PublicBaseUrl = string.IsNullOrWhiteSpace(request.PublicBaseUrl)
            ? null
            : request.PublicBaseUrl.Trim().TrimEnd('/');
        plugin.SaveConfiguration();
        return Ok(BuildUrlResponse());
    }

    /// <summary>
    /// Reports whether Jellyfin's native Live TV configuration currently contains the Spectral TV
    /// M3U tuner and XMLTV provider. This never mutates Jellyfin configuration.
    /// </summary>
    [HttpGet("livetv-status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> GetLiveTvStatus()
    {
        try
        {
            return Ok(BuildLiveTvStatus());
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Could not inspect Jellyfin Live TV configuration: {ex.Message}" });
        }
    }

    /// <summary>
    /// Explicitly connects Spectral TV to Jellyfin's native Live TV system. The operation is
    /// idempotent: it updates the tuner/provider previously recorded by Spectral TV or safely adopts
    /// an existing entry that already points at Spectral TV. Unrelated Live TV entries are untouched.
    /// </summary>
    [HttpPost("livetv")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<object>> ConnectLiveTv(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var urls = BuildUrls();
            var liveTv = GetLiveTvConfiguration();

            var tuner = FindSpectralTuner(liveTv, urls.M3uUrl)
                ?? new TunerHostInfo
                {
                    Type = M3uTunerType,
                    FriendlyName = SpectralTunerFriendlyName,
                    Url = urls.M3uUrl,
                    TunerCount = 0,
                    AllowStreamSharing = true,
                    AllowHWTranscoding = true,
                    ReadAtNativeFramerate = false
                };

            // Preserve any user-chosen tuner options when updating an existing Spectral entry.
            tuner.Type = M3uTunerType;
            tuner.FriendlyName = SpectralTunerFriendlyName;
            tuner.Url = urls.M3uUrl;
            tuner = await _tunerHostManager.SaveTunerHost(tuner, dataSourceChanged: false).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            liveTv = GetLiveTvConfiguration();
            var listings = FindSpectralListingsProvider(liveTv, urls.XmlTvUrl)
                ?? new ListingsProviderInfo
                {
                    Type = XmlTvProviderType,
                    Path = urls.XmlTvUrl
                };

            // A Spectral XMLTV source belongs only to the Spectral M3U tuner. This prevents the
            // provider from being considered for unrelated household tuners.
            listings.Type = XmlTvProviderType;
            listings.Path = urls.XmlTvUrl;
            listings.EnableAllTuners = false;
            listings.EnabledTuners = [tuner.Id];
            listings = await _listingsManager
                .SaveListingProvider(listings, validateLogin: false, validateListings: false)
                .ConfigureAwait(false);

            plugin.Configuration.LiveTvTunerHostId = tuner.Id;
            plugin.Configuration.LiveTvListingsProviderId = listings.Id;
            plugin.SaveConfiguration();

            return Ok(BuildLiveTvStatus());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499, new { message = "Live TV connection was cancelled." });
        }
        catch (Exception ex)
        {
            // This endpoint is admin-triggered and request-scoped. Never allow a Live TV setup
            // failure to become a plugin startup dependency or a Jellyfin host failure.
            return StatusCode(500, new
            {
                message = "Jellyfin Live TV could not be connected. No unrelated tuner or guide entries were changed.",
                detail = ex.Message
            });
        }
    }

    private object BuildLiveTvStatus()
    {
        var urls = BuildUrls();
        var liveTv = GetLiveTvConfiguration();
        var tuner = FindSpectralTuner(liveTv, urls.M3uUrl);
        var listings = FindSpectralListingsProvider(liveTv, urls.XmlTvUrl);

        var tunerConfigured = tuner is not null
            && string.Equals(tuner.Type, M3uTunerType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeUrl(tuner.Url), NormalizeUrl(urls.M3uUrl), StringComparison.OrdinalIgnoreCase);
        var guideConfigured = listings is not null
            && string.Equals(listings.Type, XmlTvProviderType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeUrl(listings.Path), NormalizeUrl(urls.XmlTvUrl), StringComparison.OrdinalIgnoreCase);
        var guideLinkedToTuner = tuner is not null
            && listings is not null
            && (listings.EnableAllTuners
                || (listings.EnabledTuners ?? Array.Empty<string>())
                    .Contains(tuner.Id, StringComparer.OrdinalIgnoreCase));

        return new
        {
            connected = tunerConfigured && guideConfigured && guideLinkedToTuner,
            tunerConfigured,
            guideConfigured,
            guideLinkedToTuner,
            tunerId = tuner?.Id,
            listingsProviderId = listings?.Id,
            m3uUrl = urls.M3uUrl,
            xmlTvUrl = urls.XmlTvUrl
        };
    }

    private LiveTvOptions GetLiveTvConfiguration()
        => _configurationManager.GetConfiguration<LiveTvOptions>("livetv");

    private TunerHostInfo? FindSpectralTuner(LiveTvOptions liveTv, string expectedUrl)
    {
        var tuners = liveTv.TunerHosts ?? Array.Empty<TunerHostInfo>();
        var savedId = Plugin.Instance?.Configuration.LiveTvTunerHostId;
        if (!string.IsNullOrWhiteSpace(savedId))
        {
            var saved = tuners.FirstOrDefault(t => string.Equals(t.Id, savedId, StringComparison.OrdinalIgnoreCase));
            if (saved is not null)
            {
                return saved;
            }
        }

        var byUrl = tuners.FirstOrDefault(t =>
            string.Equals(t.Type, M3uTunerType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeUrl(t.Url), NormalizeUrl(expectedUrl), StringComparison.OrdinalIgnoreCase));
        if (byUrl is not null)
        {
            return byUrl;
        }

        return tuners.FirstOrDefault(t =>
            string.Equals(t.Type, M3uTunerType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.FriendlyName, SpectralTunerFriendlyName, StringComparison.OrdinalIgnoreCase));
    }

    private ListingsProviderInfo? FindSpectralListingsProvider(LiveTvOptions liveTv, string expectedUrl)
    {
        var providers = liveTv.ListingProviders ?? Array.Empty<ListingsProviderInfo>();
        var savedId = Plugin.Instance?.Configuration.LiveTvListingsProviderId;
        if (!string.IsNullOrWhiteSpace(savedId))
        {
            var saved = providers.FirstOrDefault(p => string.Equals(p.Id, savedId, StringComparison.OrdinalIgnoreCase));
            if (saved is not null)
            {
                return saved;
            }
        }

        var byUrl = providers.FirstOrDefault(p =>
            string.Equals(p.Type, XmlTvProviderType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeUrl(p.Path), NormalizeUrl(expectedUrl), StringComparison.OrdinalIgnoreCase));
        if (byUrl is not null)
        {
            return byUrl;
        }

        return providers.FirstOrDefault(p =>
            string.Equals(p.Type, XmlTvProviderType, StringComparison.OrdinalIgnoreCase)
            && NormalizeUrl(p.Path).EndsWith("/SpectralTV/iptv/epg.xml", StringComparison.OrdinalIgnoreCase));
    }

    private object BuildUrlResponse()
    {
        var urls = BuildUrls();
        return new
        {
            baseUrl = urls.BaseUrl,
            m3u = urls.M3uUrl,
            epg = urls.XmlTvUrl
        };
    }

    private (string BaseUrl, string M3uUrl, string XmlTvUrl) BuildUrls()
    {
        var baseUrl = EpgService.GetPublicBaseUrl(Request, _appHost).TrimEnd('/');
        return (
            baseUrl,
            $"{baseUrl}/SpectralTV/iptv/channels.m3u",
            $"{baseUrl}/SpectralTV/iptv/epg.xml");
    }

    private static string NormalizeUrl(string? value)
        => (value ?? string.Empty).Trim().TrimEnd('/');
}

public class SetupSettingsRequest
{
    public string? PublicBaseUrl { get; set; }
}

/// <summary>Manual schedule rebuild endpoint.</summary>
[ApiController]
[Route("SpectralTV/api/tasks")]
[Authorize(Policy = Policies.RequiresElevation)]
public class TasksController : ControllerBase
{
    private readonly PlayoutBuilderService _playoutBuilder;

    public TasksController(PlayoutBuilderService playoutBuilder)
    {
        _playoutBuilder = playoutBuilder;
    }

    [HttpPost("rebuild-all")]
    public IActionResult RebuildAll()
    {
        _playoutBuilder.QueueForceRebuildAllChannels();
        return Accepted(new { queued = true });
    }
}
