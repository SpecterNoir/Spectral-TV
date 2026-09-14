using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SpectralTV.Data;
using Jellyfin.Plugin.SpectralTV.Domain;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpectralTV.Services;

/// <summary>
/// Materializes a user's smart on-demand channel into a normal private Jellyfin playlist.
/// The playlist is deliberately made from real Jellyfin item ids so it is visible and playable
/// through ordinary Jellyfin clients without a Spectral TV-specific client application.
/// </summary>
public class OnDemandPlaylistService
{
    private readonly SpectralTvDbContext _db;
    private readonly OnDemandSequenceService _sequence;
    private readonly IPlaylistManager _playlistManager;
    private readonly ILogger<OnDemandPlaylistService> _logger;

    public OnDemandPlaylistService(
        SpectralTvDbContext db,
        OnDemandSequenceService sequence,
        IPlaylistManager playlistManager,
        ILogger<OnDemandPlaylistService> logger)
    {
        _db = db;
        _sequence = sequence;
        _playlistManager = playlistManager;
        _logger = logger;
    }

    /// <summary>
    /// Returns the progress key used for a real Jellyfin user. Admin test keys remain separate.
    /// </summary>
    public static string GetUserProgressKey(Guid userId)
        => $"user:{userId.ToString("N", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Creates or refreshes the private Jellyfin playlist for one user/channel pair.
    /// Calling this also ensures that a current program exists in the user's Spectral TV progress.
    /// </summary>
    public async Task<OnDemandPlaylistSyncResult> SyncAsync(
        Guid channelId,
        Guid userId,
        int programCount = 24,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A Jellyfin user id is required.", nameof(userId));
        }

        var channel = await _db.OnDemandChannels
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken)
            ?? throw new InvalidOperationException("On-demand channel not found.");
        if (!channel.Enabled)
        {
            throw new InvalidOperationException("This on-demand channel is disabled.");
        }

        programCount = Math.Clamp(programCount, 4, 100);
        var progressKey = GetUserProgressKey(userId);

        // Persist a real current program, then look ahead non-destructively from that exact state.
        var current = await _sequence.GetNextAsync(channelId, progressKey, false, cancellationToken);
        var preview = await _sequence.PreviewAsync(channelId, programCount, progressKey, cancellationToken);
        var queueIds = MergeQueue(current, preview);
        if (queueIds.Count == 0)
        {
            throw new InvalidOperationException("No playable items could be generated for this on-demand channel.");
        }

        var playlistName = $"Spectral TV · {channel.Name}";
        var link = await _db.OnDemandPlaylistLinks
            .FirstOrDefaultAsync(l => l.ChannelId == channelId && l.UserId == userId, cancellationToken);

        Guid playlistId;
        var existingPlaylist = link is null
            ? null
            : _playlistManager.GetPlaylistForUser(link.JellyfinPlaylistId, userId);

        if (existingPlaylist is null)
        {
            var created = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = playlistName,
                UserId = userId,
                MediaType = MediaType.Video,
                ItemIdList = queueIds,
                Users = [],
                Public = false
            });

            if (!Guid.TryParse(created.Id, out playlistId))
            {
                throw new InvalidOperationException("Jellyfin created the playlist but returned an invalid playlist id.");
            }

            if (link is null)
            {
                link = new OnDemandPlaylistLink
                {
                    ChannelId = channelId,
                    UserId = userId,
                    JellyfinPlaylistId = playlistId,
                    QueueProgramCount = programCount,
                    LastSyncedAt = DateTime.UtcNow
                };
                _db.OnDemandPlaylistLinks.Add(link);
            }
            else
            {
                link.JellyfinPlaylistId = playlistId;
                link.QueueProgramCount = programCount;
                link.LastSyncedAt = DateTime.UtcNow;
            }

            _logger.LogInformation(
                "Created native Jellyfin playlist {PlaylistId} for Spectral TV on-demand channel {ChannelId} and user {UserId}",
                playlistId,
                channelId,
                userId);
        }
        else
        {
            playlistId = existingPlaylist.Id;
            await _playlistManager.UpdatePlaylist(new PlaylistUpdateRequest
            {
                Id = playlistId,
                UserId = userId,
                Name = playlistName,
                Ids = queueIds,
                Users = [],
                Public = false
            });

            link!.JellyfinPlaylistId = playlistId;
            link.QueueProgramCount = programCount;
            link.LastSyncedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);

        return new OnDemandPlaylistSyncResult
        {
            ChannelId = channelId,
            ChannelName = channel.Name,
            PlaylistId = playlistId,
            PlaylistName = playlistName,
            QueueItemCount = queueIds.Count,
            CurrentItemId = current.Progress.CurrentItemId,
            CurrentPositionTicks = current.Progress.CurrentPositionTicks,
            LastSyncedAt = link!.LastSyncedAt
        };
    }

    /// <summary>
    /// Gets the existing playlist link without creating or changing progress.
    /// </summary>
    public async Task<OnDemandPlaylistLink?> GetLinkAsync(
        Guid channelId,
        Guid userId,
        CancellationToken cancellationToken = default)
        => await _db.OnDemandPlaylistLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.ChannelId == channelId && l.UserId == userId, cancellationToken);

    private static List<Guid> MergeQueue(OnDemandQueueResult current, OnDemandQueueResult preview)
    {
        var result = current.Items
            .Where(i => i.JellyfinItemId != Guid.Empty)
            .Select(i => i.JellyfinItemId)
            .ToList();

        var currentItemId = current.Progress.CurrentItemId;
        var previewItems = preview.Items;
        var startIndex = -1;
        if (currentItemId.HasValue)
        {
            startIndex = previewItems.FindIndex(i =>
                string.Equals(i.Kind, "program", StringComparison.OrdinalIgnoreCase)
                && i.JellyfinItemId == currentItemId.Value);
        }

        var tail = startIndex >= 0 ? previewItems.Skip(startIndex + 1) : previewItems;
        result.AddRange(tail.Where(i => i.JellyfinItemId != Guid.Empty).Select(i => i.JellyfinItemId));
        return result;
    }
}

public class OnDemandPlaylistSyncResult
{
    public Guid ChannelId { get; set; }

    public string ChannelName { get; set; } = string.Empty;

    public Guid PlaylistId { get; set; }

    public string PlaylistName { get; set; } = string.Empty;

    public int QueueItemCount { get; set; }

    public Guid? CurrentItemId { get; set; }

    public long CurrentPositionTicks { get; set; }

    public DateTime LastSyncedAt { get; set; }
}
