using Jellyfin.Plugin.SpectralTV.Domain;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.LiveTv;

namespace Jellyfin.Plugin.SpectralTV.Services;

/// <summary>
/// Reproduces Jellyfin's M3U channel-id formula for the configured Spectral tuner.
/// </summary>
internal static class SpectralLiveTvChannelIds
{
    private const string SpectralM3uSuffix = "/SpectralTV/iptv/channels.m3u";

    public static Dictionary<Guid, string> Build(
        IServerConfigurationManager configurationManager,
        IReadOnlyList<Channel> channels)
    {
        var liveTv = configurationManager.GetConfiguration<LiveTvOptions>("livetv");
        var tuners = liveTv.TunerHosts ?? Array.Empty<TunerHostInfo>();
        var savedTunerId = Plugin.Instance?.Configuration.LiveTvTunerHostId;
        var tuner = !string.IsNullOrWhiteSpace(savedTunerId)
            ? tuners.FirstOrDefault(candidate => string.Equals(candidate.Id, savedTunerId, StringComparison.OrdinalIgnoreCase))
            : null;
        tuner ??= tuners.FirstOrDefault(candidate =>
            string.Equals(candidate.Type, "m3u", StringComparison.OrdinalIgnoreCase)
            && NormalizeUrl(candidate.Url).EndsWith(SpectralM3uSuffix, StringComparison.OrdinalIgnoreCase));

        var tunerUrl = NormalizeUrl(tuner?.Url);
        if (!tunerUrl.EndsWith(SpectralM3uSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<Guid, string>();
        }

        // M3UTunerHost prefix: "m3u_" + MD5(tuner URL). M3uParser then appends
        // MD5(the exact media URL from the playlist) for each channel.
        var baseUrl = tunerUrl[..^SpectralM3uSuffix.Length];
        var channelIdPrefix = "m3u_" + tunerUrl.GetMD5().ToString("N");
        return channels.ToDictionary(
            channel => channel.Id,
            channel => channelIdPrefix
                + $"{baseUrl}/SpectralTV/iptv/stream/{channel.Id:N}".GetMD5().ToString("N"));
    }

    private static string NormalizeUrl(string? value)
        => (value ?? string.Empty).Trim().TrimEnd('/');
}
