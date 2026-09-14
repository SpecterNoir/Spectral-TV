using System.Globalization;
using Jellyfin.Plugin.SpectralTV.Data;
using Jellyfin.Plugin.SpectralTV.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.SpectralTV.Api;

/// <summary>
/// Viewer-facing on-demand API. Unlike the admin/test API, progress is always bound to the
/// authenticated Jellyfin user and callers can never supply or modify another user's progress key.
/// </summary>
[ApiController]
[Route("SpectralTV/api/viewer/on-demand")]
[Authorize]
public class ViewerOnDemandController : ControllerBase
{
    private const string JellyfinUserIdClaim = "Jellyfin-UserId";

    private readonly SpectralTvDbContext _db;
    private readonly ILibraryManager _libraryManager;
    private readonly OnDemandSequenceService _sequence;
    private readonly OnDemandPlaylistService _playlists;

    public ViewerOnDemandController(
        SpectralTvDbContext db,
        ILibraryManager libraryManager,
        OnDemandSequenceService sequence,
        OnDemandPlaylistService playlists)
    {
        _db = db;
        _libraryManager = libraryManager;
        _sequence = sequence;
        _playlists = playlists;
    }

    /// <summary>
    /// Lists enabled Spectral TV on-demand channels with this viewer's current resume state.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<object>>> GetChannels(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new { message = "This Jellyfin session is not associated with a user." });
        }

        var progressKey = OnDemandPlaylistService.GetUserProgressKey(userId);
        var channels = await _db.OnDemandChannels
            .AsNoTracking()
            .Where(c => c.Enabled)
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);
        var progress = await _db.OnDemandProgress
            .AsNoTracking()
            .Where(p => p.ProgressKey == progressKey)
            .ToDictionaryAsync(p => p.ChannelId, cancellationToken);
        var links = await _db.OnDemandPlaylistLinks
            .AsNoTracking()
            .Where(l => l.UserId == userId)
            .ToDictionaryAsync(l => l.ChannelId, cancellationToken);

        return Ok(channels.Select(channel =>
        {
            progress.TryGetValue(channel.Id, out var state);
            links.TryGetValue(channel.Id, out var link);
            var currentItem = state?.CurrentItemId is Guid itemId ? _libraryManager.GetItemById(itemId) : null;
            return new
            {
                channel.Id,
                channel.Name,
                channel.ProgrammingMode,
                channel.RotationMode,
                currentItemId = state?.CurrentItemId,
                currentTitle = currentItem?.Name,
                resumePositionTicks = state?.CurrentPositionTicks ?? 0,
                patternIndex = state?.PatternIndex ?? 0,
                playlistId = link?.JellyfinPlaylistId,
                playlistLastSyncedAt = link?.LastSyncedAt
            };
        }));
    }

    /// <summary>
    /// Non-destructively previews the authenticated viewer's next programs.
    /// </summary>
    [HttpGet("{channelId:guid}/preview")]
    public async Task<ActionResult<OnDemandQueueResult>> Preview(
        Guid channelId,
        [FromQuery] int count = 12,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new { message = "This Jellyfin session is not associated with a user." });
        }

        try
        {
            return Ok(await _sequence.PreviewAsync(
                channelId,
                Math.Clamp(count, 1, 100),
                OnDemandPlaylistService.GetUserProgressKey(userId),
                cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Starts or resumes the viewer's current main program without advancing it.
    /// </summary>
    [HttpPost("{channelId:guid}/resume")]
    public async Task<ActionResult<OnDemandQueueResult>> Resume(Guid channelId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new { message = "This Jellyfin session is not associated with a user." });
        }

        try
        {
            return Ok(await _sequence.GetNextAsync(
                channelId,
                OnDemandPlaylistService.GetUserProgressKey(userId),
                false,
                cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Marks the current main program complete and returns the next segment for this viewer.
    /// If a native playlist exists, it is refreshed to put the new current program first.
    /// </summary>
    [HttpPost("{channelId:guid}/complete")]
    public async Task<ActionResult<OnDemandQueueResult>> Complete(Guid channelId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new { message = "This Jellyfin session is not associated with a user." });
        }

        var progressKey = OnDemandPlaylistService.GetUserProgressKey(userId);
        try
        {
            var result = await _sequence.GetNextAsync(channelId, progressKey, true, cancellationToken);
            var link = await _playlists.GetLinkAsync(channelId, userId, cancellationToken);
            if (link is not null)
            {
                await _playlists.SyncAsync(
                    channelId,
                    userId,
                    link.QueueProgramCount,
                    cancellationToken,
                    result);
            }

            return Ok(result);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Updates the viewer's position inside the active main program.
    /// </summary>
    [HttpPost("{channelId:guid}/position")]
    public async Task<IActionResult> Position(
        Guid channelId,
        [FromBody] ViewerOnDemandPositionRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new { message = "This Jellyfin session is not associated with a user." });
        }

        var updated = await _sequence.ReportPositionAsync(
            channelId,
            OnDemandPlaylistService.GetUserProgressKey(userId),
            request.ItemId,
            request.PositionTicks,
            cancellationToken);
        return updated ? NoContent() : NotFound(new { message = "No matching active on-demand item was found." });
    }

    /// <summary>
    /// Creates or refreshes a private native Jellyfin playlist for this smart channel.
    /// The result can be opened from the normal Playlists section in Jellyfin clients.
    /// </summary>
    [HttpPost("{channelId:guid}/playlist")]
    public async Task<ActionResult<OnDemandPlaylistSyncResult>> SyncPlaylist(
        Guid channelId,
        [FromQuery] int programs = 24,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new { message = "This Jellyfin session is not associated with a user." });
        }

        try
        {
            return Ok(await _playlists.SyncAsync(channelId, userId, programs, cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Resets only the authenticated viewer's progress. If a native playlist exists it is immediately
    /// rebuilt from the new beginning so the ordinary Jellyfin client surface stays in sync.
    /// </summary>
    [HttpDelete("{channelId:guid}/progress")]
    public async Task<IActionResult> ResetProgress(Guid channelId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new { message = "This Jellyfin session is not associated with a user." });
        }

        var progressKey = OnDemandPlaylistService.GetUserProgressKey(userId);
        await _sequence.ResetProgressAsync(channelId, progressKey, cancellationToken);
        var link = await _playlists.GetLinkAsync(channelId, userId, cancellationToken);
        if (link is not null)
        {
            await _playlists.SyncAsync(channelId, userId, link.QueueProgramCount, cancellationToken);
        }

        return NoContent();
    }

    private bool TryGetUserId(out Guid userId)
    {
        var value = User.Claims
            .FirstOrDefault(claim => string.Equals(claim.Type, JellyfinUserIdClaim, StringComparison.OrdinalIgnoreCase))
            ?.Value;
        return Guid.TryParse(value, out userId) && userId != Guid.Empty;
    }
}

public class ViewerOnDemandPositionRequest
{
    public Guid ItemId { get; set; }

    public long PositionTicks { get; set; }
}
