using Jellyfin.Plugin.SpectralTV.Data;
using Jellyfin.Plugin.SpectralTV.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.SpectralTV.Api;

/// <summary>
/// Viewer-facing list of the actual Spectral TV live channels published through M3U/XMLTV.
/// This intentionally reads the main Channels table rather than the separate experimental
/// on-demand smart-channel tables so the Jellyfin Home row mirrors Channel Studio exactly.
/// </summary>
[ApiController]
[Route("SpectralTV/api/viewer/channels")]
[Authorize]
public sealed class ViewerChannelsController : ControllerBase
{
    private readonly SpectralTvDbContext _db;

    public ViewerChannelsController(SpectralTvDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<object>>> GetChannels(CancellationToken cancellationToken)
    {
        var channels = await _db.Channels
            .AsNoTracking()
            .Where(channel => channel.Enabled
                && (channel.ContentType == ChannelContentType.TvShow
                    || channel.ContentType == ChannelContentType.Movie))
            .OrderBy(channel => channel.Number)
            .ThenBy(channel => channel.Name)
            .ToListAsync(cancellationToken);

        if (channels.Count == 0)
        {
            return Ok(Array.Empty<object>());
        }

        var now = DateTime.UtcNow;
        var channelIds = channels.Select(channel => channel.Id).ToArray();
        var currentItems = await _db.PlayoutItems
            .AsNoTracking()
            .Where(item => channelIds.Contains(item.ChannelId)
                && item.Start <= now
                && item.Finish > now)
            .OrderBy(item => item.Start)
            .ToListAsync(cancellationToken);

        var currentByChannel = currentItems
            .GroupBy(item => item.ChannelId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.Start).First());

        return Ok(channels.Select(channel =>
        {
            currentByChannel.TryGetValue(channel.Id, out var current);
            var logoUrl = string.IsNullOrWhiteSpace(channel.LogoFileName)
                ? null
                : $"SpectralTV/api/logos/{channel.Id:N}/{Uri.EscapeDataString(channel.LogoFileName)}";

            return new
            {
                id = channel.Id,
                number = ChannelNumbers.Format(channel.Number),
                channel.Name,
                logoUrl,
                currentTitle = current?.Title,
                currentStart = current?.Start,
                currentFinish = current?.Finish
            };
        }));
    }
}
