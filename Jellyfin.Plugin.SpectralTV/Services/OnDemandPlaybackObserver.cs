using Jellyfin.Plugin.SpectralTV.Data;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpectralTV.Services;

/// <summary>
/// Mirrors ordinary Jellyfin playback events back into Spectral TV's per-user on-demand progress.
/// Events are accepted only when the session queue can be matched to a Spectral-created native
/// playlist, preventing a user from advancing a smart channel merely by watching the same episode
/// from its normal library page.
/// </summary>
public sealed class OnDemandPlaybackObserver : IHostedService
{
    private readonly ISessionManager _sessionManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OnDemandPlaybackObserver> _logger;
    private readonly SemaphoreSlim _eventGate = new(1, 1);
    private bool _subscribed;

    public OnDemandPlaybackObserver(
        ISessionManager sessionManager,
        IServiceScopeFactory scopeFactory,
        ILogger<OnDemandPlaybackObserver> logger)
    {
        _sessionManager = sessionManager;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _sessionManager.PlaybackProgress += OnPlaybackProgress;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            _subscribed = true;
            _logger.LogInformation("Spectral TV on-demand playback observer started");
        }
        catch (Exception ex)
        {
            // Playback observation is optional. It must never make Jellyfin startup depend on Spectral TV.
            _logger.LogError(ex, "Spectral TV playback observer could not start; Jellyfin will continue normally");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscribed)
        {
            try
            {
                _sessionManager.PlaybackProgress -= OnPlaybackProgress;
                _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Spectral TV playback observer cleanup encountered an error");
            }

            _subscribed = false;
        }

        return Task.CompletedTask;
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
        => _ = ProcessEventSafelyAsync(e, completed: false);

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
        => _ = ProcessEventSafelyAsync(e, completed: e.PlayedToCompletion);

    private async Task ProcessEventSafelyAsync(PlaybackProgressEventArgs e, bool completed)
    {
        try
        {
            await _eventGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await ProcessEventAsync(e, completed).ConfigureAwait(false);
            }
            finally
            {
                _eventGate.Release();
            }
        }
        catch (Exception ex)
        {
            // Session event handlers run inside the Jellyfin host. Nothing here may escape back into it.
            _logger.LogError(ex, "Spectral TV ignored a playback event after an internal tracking error");
        }
    }

    private async Task ProcessEventAsync(PlaybackProgressEventArgs e, bool completed)
    {
        if (e.Item is null || e.Session is null)
        {
            return;
        }

        var userIds = e.Users?
            .Select(user => user.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList() ?? new List<Guid>();
        if (userIds.Count == 0 && e.Session.UserId != Guid.Empty)
        {
            userIds.Add(e.Session.UserId);
        }

        if (userIds.Count == 0)
        {
            return;
        }

        var sessionQueue = e.Session.NowPlayingQueue?
            .Select(item => item.Id)
            .Where(id => id != Guid.Empty)
            .ToList() ?? new List<Guid>();

        // A single-item queue is indistinguishable from opening the episode directly from the library.
        // Require playlist context before mutating Spectral TV progress.
        if (sessionQueue.Count < 2)
        {
            return;
        }

        foreach (var userId in userIds)
        {
            await ProcessUserEventAsync(
                userId,
                e.Item.Id,
                Math.Max(0, e.PlaybackPositionTicks ?? 0),
                completed,
                sessionQueue).ConfigureAwait(false);
        }
    }

    private async Task ProcessUserEventAsync(
        Guid userId,
        Guid itemId,
        long playbackPositionTicks,
        bool completed,
        IReadOnlyList<Guid> sessionQueue)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpectralTvDbContext>();
        var sequence = scope.ServiceProvider.GetRequiredService<OnDemandSequenceService>();
        var playlistService = scope.ServiceProvider.GetRequiredService<OnDemandPlaylistService>();
        var playlistManager = scope.ServiceProvider.GetRequiredService<IPlaylistManager>();

        var progressKey = OnDemandPlaylistService.GetUserProgressKey(userId);
        var candidates = await db.OnDemandProgress
            .AsNoTracking()
            .Where(p => p.ProgressKey == progressKey && p.CurrentItemId == itemId)
            .ToListAsync()
            .ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return;
        }

        foreach (var progress in candidates)
        {
            var link = await db.OnDemandPlaylistLinks
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.ChannelId == progress.ChannelId && l.UserId == userId)
                .ConfigureAwait(false);
            if (link is null)
            {
                continue;
            }

            var playlist = playlistManager.GetPlaylistForUser(link.JellyfinPlaylistId, userId);
            if (playlist is null)
            {
                continue;
            }

            var playlistQueue = playlist.GetManageableItems()
                .Select(entry => entry.Item2.Id)
                .Where(id => id != Guid.Empty)
                .ToList();
            if (!QueueMatchesSpectralPlaylist(itemId, sessionQueue, playlistQueue))
            {
                continue;
            }

            if (!completed)
            {
                await sequence.ReportPositionAsync(
                    progress.ChannelId,
                    progressKey,
                    itemId,
                    playbackPositionTicks)
                    .ConfigureAwait(false);
                return;
            }

            var next = await sequence.GetNextAsync(progress.ChannelId, progressKey, completeCurrent: true)
                .ConfigureAwait(false);
            await playlistService.SyncAsync(
                    progress.ChannelId,
                    userId,
                    link.QueueProgramCount,
                    CancellationToken.None,
                    next)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Advanced Spectral TV on-demand channel {ChannelId} for user {UserId} after completed item {ItemId}",
                progress.ChannelId,
                userId,
                itemId);
            return;
        }
    }

    /// <summary>
    /// Verifies that the current item plus at least one neighboring queue item line up with the
    /// linked Spectral playlist. This intentionally favors false negatives over advancing progress
    /// from unrelated playback.
    /// </summary>
    private static bool QueueMatchesSpectralPlaylist(
        Guid currentItemId,
        IReadOnlyList<Guid> sessionQueue,
        IReadOnlyList<Guid> playlistQueue)
    {
        if (sessionQueue.Count < 2 || playlistQueue.Count < 2)
        {
            return false;
        }

        var sessionIndexes = Enumerable.Range(0, sessionQueue.Count)
            .Where(index => sessionQueue[index] == currentItemId)
            .ToList();
        var playlistIndexes = Enumerable.Range(0, playlistQueue.Count)
            .Where(index => playlistQueue[index] == currentItemId)
            .ToList();

        foreach (var sessionIndex in sessionIndexes)
        {
            foreach (var playlistIndex in playlistIndexes)
            {
                var matched = 1;

                if (sessionIndex > 0 && playlistIndex > 0
                    && sessionQueue[sessionIndex - 1] == playlistQueue[playlistIndex - 1])
                {
                    matched++;
                }

                if (sessionIndex + 1 < sessionQueue.Count && playlistIndex + 1 < playlistQueue.Count
                    && sessionQueue[sessionIndex + 1] == playlistQueue[playlistIndex + 1])
                {
                    matched++;
                }

                if (matched >= 2)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
