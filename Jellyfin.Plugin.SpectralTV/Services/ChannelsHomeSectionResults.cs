using Jellyfin.Plugin.SpectralTV.Data;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpectralTV.Services;

/// <summary>
/// Result provider consumed by Home Screen Sections. Each enabled Spectral on-demand channel is
/// represented by that viewer's private native Jellyfin playlist, while the card itself is renamed
/// to the channel name so the playlist remains an implementation detail.
/// </summary>
public sealed class ChannelsHomeSectionResults
{
    private readonly SpectralTvDbContext _db;
    private readonly IPlaylistManager _playlistManager;
    private readonly IDtoService _dtoService;
    private readonly IUserManager _userManager;
    private readonly ILogger<ChannelsHomeSectionResults> _logger;

    public ChannelsHomeSectionResults(
        SpectralTvDbContext db,
        IPlaylistManager playlistManager,
        IDtoService dtoService,
        IUserManager userManager,
        ILogger<ChannelsHomeSectionResults> logger)
    {
        _db = db;
        _playlistManager = playlistManager;
        _dtoService = dtoService;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// Returns the viewer-specific Spectral on-demand channel cards.
    /// This method is synchronous because Home Screen Sections' public section contract is synchronous.
    /// </summary>
    public QueryResult<BaseItemDto> GetResults(ChannelsHomeSectionPayload payload)
    {
        if (payload.UserId == Guid.Empty)
        {
            return Empty();
        }

        var user = _userManager.GetUserById(payload.UserId);
        if (user is null)
        {
            return Empty();
        }

        try
        {
            var channels = _db.OnDemandChannels
                .AsNoTracking()
                .Where(channel => channel.Enabled)
                .OrderBy(channel => channel.Name)
                .ToList();

            if (channels.Count == 0)
            {
                return Empty();
            }

            var channelIds = channels.Select(channel => channel.Id).ToArray();
            var links = _db.OnDemandPlaylistLinks
                .AsNoTracking()
                .Where(link => link.UserId == payload.UserId && channelIds.Contains(link.ChannelId))
                .ToDictionary(link => link.ChannelId);

            var items = new List<BaseItem>();
            var channelByPlaylistId = new Dictionary<Guid, Domain.OnDemandChannel>();

            foreach (var channel in channels)
            {
                if (!links.TryGetValue(channel.Id, out var link))
                {
                    continue;
                }

                var playlist = _playlistManager.GetPlaylistForUser(link.JellyfinPlaylistId, payload.UserId);
                if (playlist is null)
                {
                    continue;
                }

                items.Add(playlist);
                channelByPlaylistId[playlist.Id] = channel;
            }

            if (items.Count == 0)
            {
                return Empty();
            }

            var dtoOptions = new DtoOptions
            {
                Fields =
                [
                    ItemFields.PrimaryImageAspectRatio,
                    ItemFields.MediaSourceCount
                ],
                ImageTypes =
                [
                    ImageType.Primary,
                    ImageType.Backdrop,
                    ImageType.Banner,
                    ImageType.Thumb
                ],
                ImageTypeLimit = 1
            };

            var dtos = _dtoService.GetBaseItemDtos(items, dtoOptions, user).ToList();
            foreach (var dto in dtos)
            {
                if (!channelByPlaylistId.TryGetValue(dto.Id, out var channel))
                {
                    continue;
                }

                // The native playlist id remains the actual item id so standard Jellyfin cards are
                // still playable on web. Spectral metadata lets the next UI layer intercept the card
                // and turn a click into direct resume instead of exposing playlist management.
                dto.Name = channel.Name;
                dto.SortName = channel.Name;
                dto.Overview = "Resume this Spectral TV on-demand channel.";
                dto.ProviderIds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                dto.ProviderIds["SpectralTvChannel"] = channel.Id.ToString("D");
                dto.ProviderIds["SpectralTvPlaylist"] = dto.Id.ToString("D");
            }

            return new QueryResult<BaseItemDto>(dtos);
        }
        catch (Exception ex)
        {
            // A home-screen convenience row must never break the user's Jellyfin home page.
            _logger.LogWarning(ex, "Could not build the Spectral TV Channels home section for user {UserId}", payload.UserId);
            return Empty();
        }
    }

    private static QueryResult<BaseItemDto> Empty()
        => new(Array.Empty<BaseItemDto>());
}

/// <summary>
/// Payload shape supplied by Home Screen Sections when resolving a plugin-defined section.
/// </summary>
public sealed class ChannelsHomeSectionPayload
{
    public Guid UserId { get; set; }

    public string AdditionalData { get; set; } = string.Empty;
}
