using System.Collections.Concurrent;
using Jellyfin.Plugin.SpectralTV.Data;
using MediaBrowser.Controller.LiveTv;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpectralTV.Services;

public class PlayoutBuilderService : BackgroundService
{
    private static readonly SemaphoreSlim ManualRebuildAllLock = new(1, 1);
    private static readonly ConcurrentDictionary<Guid, ChannelPlayoutRebuildState> RebuildStates = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IGuideManager _guideManager;
    private readonly ILogger<PlayoutBuilderService> _logger;
    private readonly SemaphoreSlim _liveTvRefreshLock = new(1, 1);
    private int _liveTvRefreshQueued;

    public PlayoutBuilderService(
        IServiceScopeFactory scopeFactory,
        IGuideManager guideManager,
        ILogger<PlayoutBuilderService> logger)
    {
        _scopeFactory = scopeFactory;
        _guideManager = guideManager;
        _logger = logger;
    }

    /// <summary>
    /// Debounces a native Jellyfin Live TV guide refresh after the Spectral M3U lineup changes.
    /// This keeps Jellyfin's TvChannel items synchronized without blocking the channel editor.
    /// </summary>
    public void QueueLiveTvRefresh()
    {
        if (Interlocked.Exchange(ref _liveTvRefreshQueued, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Collapse a burst of create/update operations into one guide refresh.
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                await _liveTvRefreshLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    _logger.LogInformation("Refreshing Jellyfin Live TV after a Spectral channel lineup change");
                    await _guideManager.RefreshGuide(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _liveTvRefreshLock.Release();
                }
            }
            catch (Exception ex)
            {
                // A missing/unconfigured tuner should not make channel editing fail. The Home row
                // remains visible and reports that playback is waiting for Live TV synchronization.
                _logger.LogWarning(ex, "Jellyfin Live TV refresh after a Spectral channel change failed");
            }
            finally
            {
                Interlocked.Exchange(ref _liveTvRefreshQueued, 0);
            }
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await BuildAllChannelsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Playout builder failed");
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    public async Task BuildAllChannelsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpectralTvDbContext>();
        var generator = scope.ServiceProvider.GetRequiredService<LineupGeneratorService>();

        await db.Database.EnsureCreatedAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var horizonEnd = PlayoutScheduleHelper.GetHorizonEndUtc(now);
        var trimBefore = now.AddDays(-2);

        var channels = await db.Channels
            .Where(c => c.Enabled && (c.ContentType == Domain.ChannelContentType.TvShow || c.ContentType == Domain.ChannelContentType.Movie))
            .ToListAsync(cancellationToken);
        foreach (var channel in channels)
        {
            var stale = await db.PlayoutItems
                .Where(p => p.ChannelId == channel.Id && p.Finish < trimBefore)
                .ToListAsync(cancellationToken);
            if (stale.Count > 0)
            {
                db.PlayoutItems.RemoveRange(stale);
            }

            var latestFinish = await db.PlayoutItems
                .Where(p => p.ChannelId == channel.Id && p.Finish > now)
                .Select(p => (DateTime?)p.Finish)
                .MaxAsync(cancellationToken);

            var hasCoverageNow = await db.PlayoutItems
                .AnyAsync(p => p.ChannelId == channel.Id && p.Start <= now && p.Finish > now, cancellationToken);

            if (latestFinish.HasValue && latestFinish.Value >= horizonEnd && hasCoverageNow)
            {
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            if (!hasCoverageNow)
            {
                var rebuildStart = now.Date;
                await generator.BuildPlayoutAsync(
                    channel,
                    rebuildStart,
                    horizonEnd,
                    PlayoutBuildMode.ReplaceWindow,
                    cancellationToken);
                await db.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Rebuilt playout for channel {Channel} from {Start} to {End} (no current coverage)",
                    channel.Name,
                    rebuildStart,
                    horizonEnd);
                continue;
            }

            var appendStart = latestFinish ?? now;
            if (appendStart < now)
            {
                appendStart = now;
            }

            await generator.BuildPlayoutAsync(
                channel,
                appendStart,
                horizonEnd,
                PlayoutBuildMode.ExtendHorizon,
                cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Extended playout for channel {Channel} from {Start} to {End}",
                channel.Name,
                appendStart,
                horizonEnd);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Replaces the playout window for every enabled channel from today through the horizon.
    /// Used by the admin Rebuild All action (not the hourly maintenance loop).
    /// </summary>
    public async Task ForceRebuildAllChannelsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpectralTvDbContext>();
        var generator = scope.ServiceProvider.GetRequiredService<LineupGeneratorService>();

        await db.Database.EnsureCreatedAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var rebuildStart = now.Date;
        var horizonEnd = PlayoutScheduleHelper.GetHorizonEndUtc(now);

        var channels = await db.Channels
            .Where(c => c.Enabled && (c.ContentType == Domain.ChannelContentType.TvShow || c.ContentType == Domain.ChannelContentType.Movie))
            .ToListAsync(cancellationToken);
        foreach (var channel in channels)
        {
            await generator.BuildPlayoutAsync(
                channel,
                rebuildStart,
                horizonEnd,
                PlayoutBuildMode.ReplaceWindow,
                cancellationToken);

            _logger.LogInformation(
                "Force rebuilt playout for channel {Channel} from {Start} to {End}",
                channel.Name,
                rebuildStart,
                horizonEnd);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Replaces the playout window for one channel from today through the horizon.
    /// </summary>
    public async Task RebuildChannelAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        using var gate = await ChannelApplyLocks.AcquireAsync(channelId, cancellationToken);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpectralTvDbContext>();
        var generator = scope.ServiceProvider.GetRequiredService<LineupGeneratorService>();

        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken)
            ?? throw new InvalidOperationException("Channel not found.");

        var start = DateTime.UtcNow.Date;
        var end = PlayoutScheduleHelper.GetHorizonEndUtc(start);
        await generator.BuildPlayoutAsync(channel, start, end, PlayoutBuildMode.ReplaceWindow, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var itemCount = await db.PlayoutItems.CountAsync(
            p => p.ChannelId == channelId && p.Finish > now,
            cancellationToken);
        var hasCoverageNow = await db.PlayoutItems.AnyAsync(
            p => p.ChannelId == channelId && p.Start <= now && p.Finish > now,
            cancellationToken);

        _logger.LogInformation(
            "Rebuilt playout for channel {Channel} from {Start} to {End}: {ItemCount} future items, on-air now={HasCoverageNow}",
            channel.Name,
            start,
            end,
            itemCount,
            hasCoverageNow);
    }

    /// <summary>
    /// Gets the latest background rebuild status for a channel, if any.
    /// </summary>
    public ChannelPlayoutRebuildState? GetRebuildState(Guid channelId)
    {
        return RebuildStates.TryGetValue(channelId, out var state) ? state : null;
    }

    /// <summary>
    /// Queues a background playout rebuild so the admin HTTP request returns immediately.
    /// </summary>
    public void QueueRebuildChannel(Guid channelId)
    {
        _logger.LogInformation("Queueing background playout rebuild for channel {ChannelId}", channelId);
        var startedAt = DateTime.UtcNow;
        RebuildStates[channelId] = new ChannelPlayoutRebuildState
        {
            State = "queued",
            StartedAtUtc = startedAt
        };

        _ = Task.Run(async () =>
        {
            RebuildStates[channelId] = new ChannelPlayoutRebuildState
            {
                State = "running",
                StartedAtUtc = startedAt
            };

            try
            {
                await RebuildChannelAsync(channelId, CancellationToken.None).ConfigureAwait(false);
                await UpdateRebuildStateAfterSuccessAsync(channelId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background playout rebuild failed for channel {ChannelId}", channelId);
                RebuildStates[channelId] = new ChannelPlayoutRebuildState
                {
                    State = "failed",
                    StartedAtUtc = startedAt,
                    FinishedAtUtc = DateTime.UtcNow,
                    Error = ex.Message
                };
            }
        });
    }

    private async Task UpdateRebuildStateAfterSuccessAsync(Guid channelId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpectralTvDbContext>();
        var now = DateTime.UtcNow;
        var playoutItemCount = await db.PlayoutItems.CountAsync(p => p.ChannelId == channelId && p.Finish > now)
            .ConfigureAwait(false);
        var hasCoverageNow = await db.PlayoutItems.AnyAsync(
                p => p.ChannelId == channelId && p.Start <= now && p.Finish > now)
            .ConfigureAwait(false);

        RebuildStates[channelId] = new ChannelPlayoutRebuildState
        {
            State = "completed",
            FinishedAtUtc = DateTime.UtcNow,
            PlayoutItemCount = playoutItemCount,
            HasCoverageNow = hasCoverageNow
        };
    }

    /// <summary>
    /// Queues a background rebuild for every enabled channel.
    /// </summary>
    public void QueueForceRebuildAllChannels()
    {
        _logger.LogInformation("Queueing background rebuild-all for enabled channels.");
        _ = Task.Run(async () =>
        {
            try
            {
                await ManualRebuildAllLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    await ForceRebuildAllChannelsAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    ManualRebuildAllLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background rebuild-all failed");
            }
        });
    }
}

/// <summary>
/// Background playout rebuild status for one channel.
/// </summary>
public sealed class ChannelPlayoutRebuildState
{
    /// <summary>
    /// Gets or sets the rebuild state: queued, running, completed, or failed.
    /// </summary>
    public string State { get; set; } = "idle";

    /// <summary>
    /// Gets or sets when the rebuild was queued or started.
    /// </summary>
    public DateTime? StartedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets when the rebuild finished.
    /// </summary>
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets how many future playout items exist after rebuild.
    /// </summary>
    public int PlayoutItemCount { get; set; }

    /// <summary>
    /// Gets or sets whether a playout item covers the current time after rebuild.
    /// </summary>
    public bool HasCoverageNow { get; set; }

    /// <summary>
    /// Gets or sets the error message when <see cref="State"/> is failed.
    /// </summary>
    public string? Error { get; set; }
}
