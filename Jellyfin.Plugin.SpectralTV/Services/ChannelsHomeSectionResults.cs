using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SpectralTV.Data;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpectralTV.Services;

/// <summary>
/// Result provider consumed by Home Screen Sections. Enabled Spectral channels are returned as
/// their corresponding native Jellyfin Live TV items so normal playback behavior is preserved.
/// </summary>
public sealed class ChannelsHomeSectionResults
{
    private readonly SpectralTvDbContext _db;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly IDtoService _dtoService;
    private readonly IUserManager _userManager;
    private readonly ILogger<ChannelsHomeSectionResults> _logger;

    public ChannelsHomeSectionResults(
        SpectralTvDbContext db,
        ILibraryManager libraryManager,
        IServerConfigurationManager configurationManager,
        IDtoService dtoService,
        IUserManager userManager,
        ILogger<ChannelsHomeSectionResults> logger)
    {
        _db = db;
        _libraryManager = libraryManager;
        _configurationManager = configurationManager;
        _dtoService = dtoService;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// Returns native Jellyfin Live TV cards for the enabled Spectral channels.
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
            var channels = _db.Channels
                .AsNoTracking()
                .Where(channel => channel.Enabled
                    && (channel.ContentType == Domain.ChannelContentType.TvShow
                        || channel.ContentType == Domain.ChannelContentType.Movie))
                .OrderBy(channel => channel.Number)
                .ThenBy(channel => channel.Name)
                .ToList();

            if (channels.Count == 0)
            {
                return Empty();
            }

            var nativeItems = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    IncludeItemTypes = [BaseItemKind.LiveTvChannel]
                })
                .OfType<LiveTvChannel>()
                .ToList();
            var resolved = SpectralLiveTvChannelIds.Resolve(
                _configurationManager,
                channels,
                nativeItems);

            var items = channels
                .Where(channel => resolved.ContainsKey(channel.Id))
                .OrderBy(channel => channel.Number)
                .Select(channel => (BaseItem)resolved[channel.Id])
                .ToList();

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

            return new QueryResult<BaseItemDto>(_dtoService.GetBaseItemDtos(items, dtoOptions, user));
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
