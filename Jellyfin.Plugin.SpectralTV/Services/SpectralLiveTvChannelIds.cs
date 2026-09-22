using Jellyfin.Plugin.SpectralTV.Domain;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;

namespace Jellyfin.Plugin.SpectralTV.Services;

/// <summary>
/// Resolves Spectral M3U entries to the exact native Jellyfin Live TV items created from them.
/// </summary>
internal static class SpectralLiveTvChannelIds
{
    private const string SpectralM3uSuffix = "/SpectralTV/iptv/channels.m3u";
    private const string SpectralStreamPrefix = "/SpectralTV/iptv/stream/";

    public static Dictionary<Guid, string> Build(
        IServerConfigurationManager configurationManager,
        IReadOnlyList<Channel> channels)
    {
        var liveTv = configurationManager.GetConfiguration<LiveTvOptions>("livetv");
        var tuners = liveTv.TunerHosts ?? Array.Empty<TunerHostInfo>();
        var savedTunerId = Plugin.Instance?.Configuration.LiveTvTunerHostId;
        var tuner = !string.IsNullOrWhiteSpace(savedTunerId)
            ? tuners.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, savedTunerId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Type, "m3u", StringComparison.OrdinalIgnoreCase))
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
                + $"{baseUrl}{SpectralStreamPrefix}{channel.Id:N}".GetMD5().ToString("N"));
    }

    /// <summary>
    /// Resolves channels first by Jellyfin's exact M3U ExternalId formula and then by the
    /// Spectral stream path embedded in the imported LiveTvChannel. The path fallback makes the
    /// mapping resilient when Jellyfin rewrites/canonicalizes a tuner URL during an upgrade.
    /// </summary>
    public static Dictionary<Guid, LiveTvChannel> Resolve(
        IServerConfigurationManager configurationManager,
        IReadOnlyList<Channel> channels,
        IEnumerable<LiveTvChannel> liveTvItems)
    {
        var items = liveTvItems.ToList();
        var externalIds = Build(configurationManager, channels);
        var byExternalId = items
            .Where(item => !string.IsNullOrWhiteSpace(item.ExternalId))
            .GroupBy(item => item.ExternalId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<Guid, LiveTvChannel>();
        foreach (var channel in channels)
        {
            if (externalIds.TryGetValue(channel.Id, out var externalId)
                && byExternalId.TryGetValue(externalId, out var exact))
            {
                result[channel.Id] = exact;
                continue;
            }

            var streamSuffix = $"{SpectralStreamPrefix}{channel.Id:N}";
            var byPath = items.FirstOrDefault(item => PathMatches(item.Path, streamSuffix));
            if (byPath is not null)
            {
                result[channel.Id] = byPath;
            }
        }

        return result;
    }

    private static bool PathMatches(string? path, string streamSuffix)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            return uri.AbsolutePath.EndsWith(streamSuffix, StringComparison.OrdinalIgnoreCase);
        }

        var normalized = path.Split('?', 2)[0].Trim().TrimEnd('/');
        return normalized.EndsWith(streamSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeUrl(string? value)
        => (value ?? string.Empty).Trim().TrimEnd('/');
}
