using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpectralTV.Services;

/// <summary>
/// Owns Spectral TV's native Jellyfin Live TV tuner/listings registration.
/// Internal tuner URLs deliberately use Jellyfin's loopback API address so a browser-facing
/// reverse proxy, Synology QuickConnect hostname, or public base URL can never prevent the
/// server from fetching its own M3U/XMLTV feeds.
/// </summary>
public sealed class LiveTvIntegrationService
{
    public const string SpectralTunerFriendlyName = "Spectral TV";
    public const string M3uTunerType = "m3u";
    public const string XmlTvProviderType = "xmltv";
    public const string SpectralM3uSuffix = "/SpectralTV/iptv/channels.m3u";
    public const string SpectralXmlTvSuffix = "/SpectralTV/iptv/epg.xml";

    private readonly IServerApplicationHost _appHost;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly ITunerHostManager _tunerHostManager;
    private readonly IListingsManager _listingsManager;
    private readonly ILogger<LiveTvIntegrationService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LiveTvIntegrationService(
        IServerApplicationHost appHost,
        IServerConfigurationManager configurationManager,
        ITunerHostManager tunerHostManager,
        IListingsManager listingsManager,
        ILogger<LiveTvIntegrationService> logger)
    {
        _appHost = appHost;
        _configurationManager = configurationManager;
        _tunerHostManager = tunerHostManager;
        _listingsManager = listingsManager;
        _logger = logger;
    }

    /// <summary>
    /// Returns URLs that Jellyfin itself can always reach, including when the user accesses
    /// Jellyfin through a reverse proxy or QuickConnect.
    /// </summary>
    public LiveTvIntegrationUrls GetInternalUrls()
    {
        var baseUrl = _appHost
            .GetLocalApiUrl("127.0.0.1", Uri.UriSchemeHttp, _appHost.HttpPort)
            .TrimEnd('/');

        return new LiveTvIntegrationUrls(
            baseUrl,
            $"{baseUrl}{SpectralM3uSuffix}",
            $"{baseUrl}{SpectralXmlTvSuffix}");
    }

    public LiveTvIntegrationStatus GetStatus()
    {
        var urls = GetInternalUrls();
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

        return new LiveTvIntegrationStatus(
            tunerConfigured && guideConfigured && guideLinkedToTuner,
            tunerConfigured,
            guideConfigured,
            guideLinkedToTuner,
            tuner?.Id,
            listings?.Id,
            urls.M3uUrl,
            urls.XmlTvUrl);
    }

    /// <summary>
    /// Idempotently creates or repairs Spectral TV's native tuner and XMLTV provider.
    /// Returns true only when Jellyfin configuration was changed.
    /// </summary>
    public async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var plugin = Plugin.Instance ?? throw new InvalidOperationException("Spectral TV is not initialized.");
            var urls = GetInternalUrls();
            var liveTv = GetLiveTvConfiguration();
            var tuner = FindSpectralTuner(liveTv, urls.M3uUrl);

            var tunerNeedsSave = tuner is null
                || !string.Equals(tuner.Type, M3uTunerType, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(tuner.FriendlyName, SpectralTunerFriendlyName, StringComparison.Ordinal)
                || !string.Equals(NormalizeUrl(tuner.Url), NormalizeUrl(urls.M3uUrl), StringComparison.OrdinalIgnoreCase);

            tuner ??= new TunerHostInfo
            {
                Type = M3uTunerType,
                FriendlyName = SpectralTunerFriendlyName,
                Url = urls.M3uUrl,
                TunerCount = 0,
                AllowStreamSharing = true,
                AllowHWTranscoding = true,
                ReadAtNativeFramerate = false
            };

            if (tunerNeedsSave)
            {
                tuner.Type = M3uTunerType;
                tuner.FriendlyName = SpectralTunerFriendlyName;
                tuner.Url = urls.M3uUrl;

                // dataSourceChanged=true is intentional. Moving from a browser-facing URL to
                // loopback must invalidate Jellyfin's old M3U channel cache.
                tuner = await _tunerHostManager
                    .SaveTunerHost(tuner, dataSourceChanged: true)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            liveTv = GetLiveTvConfiguration();
            var listings = FindSpectralListingsProvider(liveTv, urls.XmlTvUrl);

            var guideNeedsSave = listings is null
                || !string.Equals(listings.Type, XmlTvProviderType, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(NormalizeUrl(listings.Path), NormalizeUrl(urls.XmlTvUrl), StringComparison.OrdinalIgnoreCase)
                || listings.EnableAllTuners
                || !(listings.EnabledTuners ?? Array.Empty<string>())
                    .Contains(tuner.Id, StringComparer.OrdinalIgnoreCase)
                || (listings.EnabledTuners ?? Array.Empty<string>()).Length != 1;

            listings ??= new ListingsProviderInfo
            {
                Type = XmlTvProviderType,
                Path = urls.XmlTvUrl
            };

            if (guideNeedsSave)
            {
                listings.Type = XmlTvProviderType;
                listings.Path = urls.XmlTvUrl;
                listings.EnableAllTuners = false;
                listings.EnabledTuners = [tuner.Id];

                listings = await _listingsManager
                    .SaveListingProvider(listings, validateLogin: false, validateListings: false)
                    .ConfigureAwait(false);
            }

            var idsChanged = !string.Equals(plugin.Configuration.LiveTvTunerHostId, tuner.Id, StringComparison.Ordinal)
                || !string.Equals(plugin.Configuration.LiveTvListingsProviderId, listings.Id, StringComparison.Ordinal);
            if (idsChanged)
            {
                plugin.Configuration.LiveTvTunerHostId = tuner.Id;
                plugin.Configuration.LiveTvListingsProviderId = listings.Id;
                plugin.SaveConfiguration();
            }

            if (tunerNeedsSave || guideNeedsSave || idsChanged)
            {
                _logger.LogInformation(
                    "Spectral TV connected Jellyfin Live TV through the local server API ({M3uUrl})",
                    urls.M3uUrl);
                return true;
            }

            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private LiveTvOptions GetLiveTvConfiguration()
        => _configurationManager.GetConfiguration<LiveTvOptions>("livetv");

    private TunerHostInfo? FindSpectralTuner(LiveTvOptions liveTv, string expectedUrl)
    {
        var tuners = liveTv.TunerHosts ?? Array.Empty<TunerHostInfo>();
        var savedId = Plugin.Instance?.Configuration.LiveTvTunerHostId;
        if (!string.IsNullOrWhiteSpace(savedId))
        {
            var saved = tuners.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, savedId, StringComparison.OrdinalIgnoreCase));
            if (saved is not null && string.Equals(saved.Type, M3uTunerType, StringComparison.OrdinalIgnoreCase))
            {
                return saved;
            }
        }

        return tuners.FirstOrDefault(candidate =>
            string.Equals(candidate.Type, M3uTunerType, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(NormalizeUrl(candidate.Url), NormalizeUrl(expectedUrl), StringComparison.OrdinalIgnoreCase)
                || NormalizeUrl(candidate.Url).EndsWith(SpectralM3uSuffix, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.FriendlyName, SpectralTunerFriendlyName, StringComparison.OrdinalIgnoreCase)));
    }

    private ListingsProviderInfo? FindSpectralListingsProvider(LiveTvOptions liveTv, string expectedUrl)
    {
        var providers = liveTv.ListingProviders ?? Array.Empty<ListingsProviderInfo>();
        var savedId = Plugin.Instance?.Configuration.LiveTvListingsProviderId;
        if (!string.IsNullOrWhiteSpace(savedId))
        {
            var saved = providers.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, savedId, StringComparison.OrdinalIgnoreCase));
            if (saved is not null && string.Equals(saved.Type, XmlTvProviderType, StringComparison.OrdinalIgnoreCase))
            {
                return saved;
            }
        }

        return providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Type, XmlTvProviderType, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(NormalizeUrl(candidate.Path), NormalizeUrl(expectedUrl), StringComparison.OrdinalIgnoreCase)
                || NormalizeUrl(candidate.Path).EndsWith(SpectralXmlTvSuffix, StringComparison.OrdinalIgnoreCase)));
    }

    private static string NormalizeUrl(string? value)
        => (value ?? string.Empty).Trim().TrimEnd('/');
}

public sealed record LiveTvIntegrationUrls(
    string BaseUrl,
    string M3uUrl,
    string XmlTvUrl);

public sealed record LiveTvIntegrationStatus(
    bool Connected,
    bool TunerConfigured,
    bool GuideConfigured,
    bool GuideLinkedToTuner,
    string? TunerId,
    string? ListingsProviderId,
    string M3uUrl,
    string XmlTvUrl);
