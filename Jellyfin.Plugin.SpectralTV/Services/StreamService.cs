using System.Collections.Concurrent;
using System.Text;
using CliWrap;
using Jellyfin.Plugin.SpectralTV.Data;
using Jellyfin.Plugin.SpectralTV.Domain;
using Jellyfin.Plugin.SpectralTV.Streaming;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpectralTV.Services;

public class StreamService
{
    private readonly ConcurrentDictionary<Guid, int> _activeStreams = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FfmpegCommandBuilder _ffmpeg;
    private readonly ILogger<StreamService> _logger;
    private readonly IMediaEncoder _mediaEncoder;

    public StreamService(
        IServiceScopeFactory scopeFactory,
        FfmpegCommandBuilder ffmpeg,
        ILogger<StreamService> logger,
        IMediaEncoder mediaEncoder)
    {
        _scopeFactory = scopeFactory;
        _ffmpeg = ffmpeg;
        _logger = logger;
        _mediaEncoder = mediaEncoder;
    }

    public async Task StreamChannelAsync(Guid channelId, Stream output, CancellationToken cancellationToken)
    {
        using var streamLease = TrackStream(channelId);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpectralTvDbContext>();

        var channel = await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            throw new InvalidOperationException("Channel not found.");
        }

        var ffmpegPath = _mediaEncoder.EncoderPath;

        while (!cancellationToken.IsCancellationRequested)
        {
            var current = await GetCurrentItemAsync(channelId, cancellationToken);
            if (current is not null)
            {
                try
                {
                    if (current.JellyfinItemId.HasValue)
                    {
                        await StreamMediaItemAsync(channel, current, ffmpegPath, output, cancellationToken);
                    }
                    else
                    {
                        await WriteFallbackAsync(channel, ffmpegPath, output, 180, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed streaming item {Title}", current.Title);
                    await WriteFallbackAsync(channel, ffmpegPath, output, 120, cancellationToken);
                }

                continue;
            }

            var fallbackDuration = await GetFallbackDurationSecondsAsync(channelId, cancellationToken);
            await WriteFallbackAsync(channel, ffmpegPath, output, fallbackDuration, cancellationToken);
        }
    }

    public async Task<PlayoutItem?> GetCurrentItemAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpectralTvDbContext>();
        var now = DateTime.UtcNow;
        return await db.PlayoutItems
            .AsNoTracking()
            .Where(p => p.ChannelId == channelId && p.Start <= now && p.Finish > now)
            .OrderByDescending(p => p.Start)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public IReadOnlyList<ChannelStreamStatus> GetActiveStreams()
    {
        return _activeStreams
            .Where(kvp => kvp.Value > 0)
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => new ChannelStreamStatus
            {
                ChannelId = kvp.Key,
                ViewerCount = kvp.Value
            })
            .ToList();
    }

    public int GetActiveStreamCount(Guid channelId)
    {
        return _activeStreams.TryGetValue(channelId, out var count) ? count : 0;
    }

    private StreamLease TrackStream(Guid channelId)
    {
        _activeStreams.AddOrUpdate(channelId, 1, (_, count) => count + 1);
        return new StreamLease(this, channelId);
    }

    private void ReleaseStream(Guid channelId)
    {
        _activeStreams.AddOrUpdate(channelId, 0, (_, count) => Math.Max(0, count - 1));
        if (_activeStreams.TryGetValue(channelId, out var remaining) && remaining == 0)
        {
            _activeStreams.TryRemove(channelId, out _);
        }
    }

    private sealed class StreamLease : IDisposable
    {
        private readonly StreamService _service;
        private readonly Guid _channelId;
        private int _disposed;

        public StreamLease(StreamService service, Guid channelId)
        {
            _service = service;
            _channelId = channelId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _service.ReleaseStream(_channelId);
        }
    }

    private async Task<double> GetFallbackDurationSecondsAsync(Guid channelId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpectralTvDbContext>();
        var now = DateTime.UtcNow;
        var nextStart = await db.PlayoutItems
            .AsNoTracking()
            .Where(p => p.ChannelId == channelId && p.Start > now)
            .OrderBy(p => p.Start)
            .Select(p => p.Start)
            .FirstOrDefaultAsync(cancellationToken);

        if (nextStart == default)
        {
            return 600;
        }

        return Math.Clamp((nextStart - now).TotalSeconds, 30, 600);
    }

    private async Task StreamMediaItemAsync(
        Channel channel,
        PlayoutItem item,
        string ffmpegPath,
        Stream output,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var libraryManager = scope.ServiceProvider.GetRequiredService<ILibraryManager>();
        var mediaItem = libraryManager.GetItemById(item.JellyfinItemId!.Value);
        if (mediaItem is null)
        {
            throw new InvalidOperationException($"Media item {item.JellyfinItemId} not found.");
        }

        var inputPath = mediaItem.Path;
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            throw new FileNotFoundException($"Media path missing for {item.Title}.");
        }

        var offset = Math.Max(0, (DateTime.UtcNow - item.Start).TotalSeconds + item.InPoint.TotalSeconds);
        var duration = Math.Max(1, (item.Finish - DateTime.UtcNow).TotalSeconds);
        var bugPath = ResolveBugPath(channel);
        var args = _ffmpeg.BuildMediaCommand(channel, inputPath, offset, duration, bugPath);

        await RunFfmpegToStreamAsync(ffmpegPath, args, output, cancellationToken);
    }

    private async Task WriteFallbackAsync(
        Channel channel,
        string ffmpegPath,
        Stream output,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        var args = _ffmpeg.BuildFallbackCommand(channel, durationSeconds);
        await RunFfmpegToStreamAsync(ffmpegPath, args, output, cancellationToken);
    }

    private static string? ResolveBugPath(Channel channel)
    {
        if (channel.BugPlacement == BugPlacementMode.None)
        {
            return null;
        }

        return channel.ChannelLogoPath;
    }

    private static async Task RunFfmpegToStreamAsync(string ffmpegPath, IReadOnlyList<string> args, Stream output, CancellationToken cancellationToken)
    {
        await CliWrap.Cli.Wrap(ffmpegPath)
            .WithArguments(args)
            .WithStandardOutputPipe(CliWrap.PipeTarget.ToStream(output))
            .WithStandardErrorPipe(CliWrap.PipeTarget.ToStringBuilder(new StringBuilder()))
            .WithValidation(CliWrap.CommandResultValidation.None)
            .ExecuteAsync(cancellationToken);
    }
}
