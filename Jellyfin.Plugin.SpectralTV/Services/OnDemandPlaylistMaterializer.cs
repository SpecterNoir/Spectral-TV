using Jellyfin.Plugin.SpectralTV.Data;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpectralTV.Services;

/// <summary>
/// Keeps enabled Spectral TV on-demand channels materialized as private Jellyfin playlists for each
/// Jellyfin user. This is intentionally a delayed/background convenience layer: any failure is logged
/// and contained, and Jellyfin startup never waits on playlist generation.
/// </summary>
public sealed class OnDemandPlaylistMaterializer : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OnDemandPlaylistMaterializer> _logger;

    public OnDemandPlaylistMaterializer(
        IServiceScopeFactory scopeFactory,
        ILogger<OnDemandPlaylistMaterializer> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncAllAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Spectral TV background playlist refresh failed; the next scheduled refresh will retry");
            }

            try
            {
                await Task.Delay(RefreshInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task SyncAllAsync(CancellationToken cancellationToken)
    {
        List<Guid> channelIds;
        List<Guid> userIds;
        using (var discoveryScope = _scopeFactory.CreateScope())
        {
            var db = discoveryScope.ServiceProvider.GetRequiredService<SpectralTvDbContext>();
            var userManager = discoveryScope.ServiceProvider.GetRequiredService<IUserManager>();
            channelIds = await db.OnDemandChannels
                .AsNoTracking()
                .Where(channel => channel.Enabled)
                .Select(channel => channel.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            userIds = userManager.GetUsersIds().Where(id => id != Guid.Empty).Distinct().ToList();
        }

        if (channelIds.Count == 0 || userIds.Count == 0)
        {
            return;
        }

        foreach (var channelId in channelIds)
        {
            foreach (var userId in userIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // A fresh scope per pair prevents one bad/missing media item from leaving a shared
                    // EF context in a state that could affect another household user's channel.
                    using var pairScope = _scopeFactory.CreateScope();
                    var playlists = pairScope.ServiceProvider.GetRequiredService<OnDemandPlaylistService>();
                    var existing = await playlists.GetLinkAsync(channelId, userId, cancellationToken).ConfigureAwait(false);
                    await playlists.SyncAsync(
                        channelId,
                        userId,
                        existing?.QueueProgramCount ?? 24,
                        cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Could not refresh Spectral TV on-demand channel {ChannelId} for Jellyfin user {UserId}",
                        channelId,
                        userId);
                }
            }
        }
    }
}
