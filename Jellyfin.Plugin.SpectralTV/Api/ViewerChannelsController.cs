using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SpectralTV.Data;
using Jellyfin.Plugin.SpectralTV.Domain;
using Jellyfin.Plugin.SpectralTV.Services;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.SpectralTV.Api;

/// <summary>
/// Viewer-facing catalog of the real always-on channels created in Channel Studio.
/// </summary>
[ApiController]
[Route("SpectralTV/api/viewer/channels")]
[Authorize]
public sealed class ViewerChannelsController : ControllerBase
{
    private readonly SpectralTvDbContext _db;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly PlayoutBuilderService _playoutBuilder;

    public ViewerChannelsController(
        SpectralTvDbContext db,
        ILibraryManager libraryManager,
        IServerConfigurationManager configurationManager,
        PlayoutBuilderService playoutBuilder)
    {
        _db = db;
        _libraryManager = libraryManager;
        _configurationManager = configurationManager;
        _playoutBuilder = playoutBuilder;
    }

    /// <summary>
    /// Lists enabled Spectral channels, their current programs, and their corresponding
    /// native Jellyfin Live TV item ids when Jellyfin has completed an M3U channel refresh.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ViewerChannelDto>>> GetChannels(CancellationToken cancellationToken)
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
            return Ok(Array.Empty<ViewerChannelDto>());
        }

        var channelIds = channels.Select(channel => channel.Id).ToArray();
        var now = DateTime.UtcNow;
        var currentItems = (await _db.PlayoutItems
                .AsNoTracking()
                .Where(item => channelIds.Contains(item.ChannelId)
                    && item.Start <= now
                    && item.Finish > now)
                .OrderByDescending(item => item.Start)
                .ToListAsync(cancellationToken))
            .GroupBy(item => item.ChannelId)
            .ToDictionary(group => group.Key, group => group.First());

        // Jellyfin's M3U parser builds ExternalId from the tuner URL plus the stream URL; tvg-id
        // is only guide metadata. Reproduce Jellyfin's exact formula so similarly named channels
        // from another tuner can never be selected by mistake.
        var externalIdByChannel = SpectralLiveTvChannelIds.Build(_configurationManager, channels);
        var spectralExternalIds = externalIdByChannel.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var liveTvItems = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.LiveTvChannel]
            })
            .OfType<LiveTvChannel>()
            .Where(item => !string.IsNullOrWhiteSpace(item.ExternalId)
                && spectralExternalIds.Contains(item.ExternalId))
            .GroupBy(item => item.ExternalId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        if (liveTvItems.Count < channels.Count)
        {
            // Self-heal the state shown in the failure recording: Spectral has channels, but
            // Jellyfin's native Live TV catalog has not refreshed since they were created.
            _playoutBuilder.QueueLiveTvRefresh();
        }

        return Ok(channels.Select(channel =>
        {
            currentItems.TryGetValue(channel.Id, out var current);
            LiveTvChannel? liveTvItem = null;
            if (externalIdByChannel.TryGetValue(channel.Id, out var externalId))
            {
                liveTvItems.TryGetValue(externalId, out liveTvItem);
            }

            return new ViewerChannelDto
            {
                Id = channel.Id,
                Number = ChannelNumbers.Format(channel.Number),
                Name = channel.Name,
                LogoUrl = string.IsNullOrWhiteSpace(channel.LogoFileName)
                    ? null
                    : $"SpectralTV/api/logos/{channel.Id:N}/{Uri.EscapeDataString(channel.LogoFileName)}",
                CurrentTitle = current?.Title,
                CurrentStart = current?.Start,
                CurrentFinish = current?.Finish,
                ScheduleReady = channel.LastPlayoutBuiltAt.HasValue,
                LiveTvItemId = liveTvItem?.Id
            };
        }).ToList());
    }
}

public sealed class ViewerChannelDto
{
    public Guid Id { get; set; }

    public string Number { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? LogoUrl { get; set; }

    public string? CurrentTitle { get; set; }

    public DateTime? CurrentStart { get; set; }

    public DateTime? CurrentFinish { get; set; }

    public bool ScheduleReady { get; set; }

    public Guid? LiveTvItemId { get; set; }
}
