using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SpectralTV.Data;
using Jellyfin.Plugin.SpectralTV.Domain;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.SpectralTV.Services;

/// <summary>
/// Builds continuous virtual-TV playout from weighted program sources and a separate filler pool.
/// Program weights are balanced by actual airtime, not by a naive per-pick random percentage.
/// </summary>
public class WeightedProgrammingService
{
    private readonly SpectralTvDbContext _db;
    private readonly ILibraryManager _libraryManager;
    private readonly ChannelService _channelService;

    public WeightedProgrammingService(
        SpectralTvDbContext db,
        ILibraryManager libraryManager,
        ChannelService channelService)
    {
        _db = db;
        _libraryManager = libraryManager;
        _channelService = channelService;
    }

    public async Task<bool> IsEnabledAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        return await _db.ChannelProgrammingSettings
            .AsNoTracking()
            .AnyAsync(s => s.ChannelId == channelId && s.Enabled, cancellationToken);
    }

    public async Task<ChannelProgrammingSettings> GetSettingsAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        return await _db.ChannelProgrammingSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ChannelId == channelId, cancellationToken)
            ?? new ChannelProgrammingSettings { ChannelId = channelId };
    }

    public async Task<ChannelProgrammingSettings> SaveSettingsAsync(
        Guid channelId,
        ChannelProgrammingSettings requested,
        CancellationToken cancellationToken = default)
    {
        var settings = await _db.ChannelProgrammingSettings
            .FirstOrDefaultAsync(s => s.ChannelId == channelId, cancellationToken);
        if (settings is null)
        {
            settings = new ChannelProgrammingSettings { ChannelId = channelId };
            _db.ChannelProgrammingSettings.Add(settings);
        }

        settings.Enabled = requested.Enabled;
        settings.FillerEnabled = requested.FillerEnabled;
        settings.FillerChancePercent = Math.Clamp(requested.FillerChancePercent, 0, 100);
        settings.MinFillerItems = Math.Clamp(requested.MinFillerItems, 0, 10);
        settings.MaxFillerItems = Math.Clamp(requested.MaxFillerItems, settings.MinFillerItems, 10);
        settings.MaxFillerSeconds = Math.Clamp(requested.MaxFillerSeconds, 0, 3600);
        settings.FillerRepeatWindow = Math.Clamp(requested.FillerRepeatWindow, 0, 100);

        await _db.SaveChangesAsync(cancellationToken);
        return settings;
    }

    public async Task BuildPlayoutAsync(
        Channel channel,
        DateTime startUtc,
        DateTime endUtc,
        PlayoutBuildMode mode,
        CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(channel.Id, cancellationToken);
        if (!settings.Enabled)
        {
            return;
        }

        var sources = await _db.ChannelProgramSources
            .AsNoTracking()
            .Where(s => s.ChannelId == channel.Id && s.Enabled && s.TargetAirtimePercent > 0)
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);
        if (sources.Count == 0)
        {
            return;
        }

        var fillers = settings.FillerEnabled
            ? await _db.ChannelFillerSources
                .AsNoTracking()
                .Where(f => f.ChannelId == channel.Id && f.Enabled && f.Weight > 0)
                .OrderBy(f => f.SortOrder)
                .ThenBy(f => f.Id)
                .ToListAsync(cancellationToken)
            : new List<ChannelFillerSource>();

        var anchor = await _channelService.GetAnchorAsync<PlayoutAnchorState>(channel.Id, cancellationToken)
            ?? new PlayoutAnchorState();

        if (mode == PlayoutBuildMode.ReplaceWindow)
        {
            var existing = await _db.PlayoutItems
                .Where(p => p.ChannelId == channel.Id && p.Finish > startUtc && p.Start < endUtc)
                .OrderBy(p => p.Start)
                .ToListAsync(cancellationToken);

            RestoreSequentialCursorsFromExistingWindow(sources, existing, anchor);
            anchor.RecentFillerSourceIds.Clear();
            _db.PlayoutItems.RemoveRange(existing);

            // PlayoutHistory in recovered SpectralTV represents generated airings as well as past airings.
            // Remove entries for the window we are replacing so manual rebuilds do not create duplicates.
            var futureHistory = await _db.PlayoutHistory
                .Where(h => h.ChannelId == channel.Id && h.AiredAt >= startUtc && h.AiredAt < endUtc)
                .ToListAsync(cancellationToken);
            _db.PlayoutHistory.RemoveRange(futureHistory);
        }

        var cursor = startUtc;
        if (mode == PlayoutBuildMode.ExtendHorizon)
        {
            var latestFinish = await _db.PlayoutItems
                .Where(p => p.ChannelId == channel.Id && p.Finish > startUtc)
                .Select(p => (DateTime?)p.Finish)
                .MaxAsync(cancellationToken);
            if (latestFinish.HasValue && latestFinish.Value > cursor)
            {
                cursor = latestFinish.Value;
            }
        }

        var airtimeSeconds = sources.ToDictionary(s => s.Id, _ => 0d);
        var fairnessStart = cursor.AddHours(-24);
        var recentProgramItems = await _db.PlayoutItems
            .AsNoTracking()
            .Where(p => p.ChannelId == channel.Id
                && p.ProgramSourceId.HasValue
                && p.FillerKind == FillerKind.None
                && p.Finish > fairnessStart
                && p.Start < cursor)
            .ToListAsync(cancellationToken);
        foreach (var item in recentProgramItems)
        {
            if (item.ProgramSourceId.HasValue && airtimeSeconds.ContainsKey(item.ProgramSourceId.Value))
            {
                airtimeSeconds[item.ProgramSourceId.Value] += Math.Max(1, (item.Finish - item.Start).TotalSeconds);
            }
        }

        var unavailable = new HashSet<Guid>();
        var sequence = 0;
        while (cursor < endUtc && unavailable.Count < sources.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = sources.Where(s => !unavailable.Contains(s.Id)).ToList();
            if (available.Count == 0)
            {
                break;
            }

            var source = PickFairestSource(channel, available, airtimeSeconds, cursor, sequence);
            var resolved = ResolveProgramItem(channel, source, anchor, cursor, sequence);
            if (resolved is null)
            {
                unavailable.Add(source.Id);
                continue;
            }

            var duration = resolved.Duration > TimeSpan.Zero ? resolved.Duration : TimeSpan.FromMinutes(30);
            var programEnd = cursor.Add(duration);
            _db.PlayoutItems.Add(new PlayoutItem
            {
                ChannelId = channel.Id,
                ProgramSourceId = source.Id,
                JellyfinItemId = resolved.Item.Id,
                Start = cursor,
                Finish = programEnd,
                Title = resolved.Title,
                FillerKind = FillerKind.None
            });
            _db.PlayoutHistory.Add(new PlayoutHistoryEntry
            {
                ChannelId = channel.Id,
                ProgramSourceId = source.Id,
                JellyfinItemId = resolved.Item.Id,
                AiredAt = cursor,
                Title = resolved.Title
            });

            airtimeSeconds[source.Id] += Math.Max(1, duration.TotalSeconds);
            cursor = programEnd;

            if (fillers.Count > 0 && settings.FillerEnabled && cursor < endUtc)
            {
                cursor = AddFillers(channel, settings, fillers, anchor, cursor, sequence);
            }

            sequence++;
        }

        await _channelService.SaveAnchorAsync(channel.Id, anchor, cancellationToken);
        await _db.Channels
            .Where(c => c.Id == channel.Id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(c => c.LastPlayoutBuiltAt, DateTime.UtcNow),
                cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private void RestoreSequentialCursorsFromExistingWindow(
        IReadOnlyList<ChannelProgramSource> sources,
        IReadOnlyList<PlayoutItem> existing,
        PlayoutAnchorState anchor)
    {
        foreach (var source in sources)
        {
            if (source.PlaybackMode != ProgramPlaybackMode.Sequential)
            {
                continue;
            }

            var sourceItem = _libraryManager.GetItemById(source.JellyfinItemId);
            if (sourceItem is not Series && sourceItem is not Season)
            {
                continue;
            }

            var episodes = GetEpisodes(sourceItem);
            if (episodes.Count == 0)
            {
                anchor.ProgramSourceCursor[source.Id] = 0;
                continue;
            }

            var firstExisting = existing
                .FirstOrDefault(p => p.ProgramSourceId == source.Id && p.JellyfinItemId.HasValue);
            if (firstExisting?.JellyfinItemId is Guid firstId)
            {
                var index = episodes.FindIndex(e => e.Id == firstId);
                anchor.ProgramSourceCursor[source.Id] = index >= 0 ? index : 0;
            }
            else
            {
                anchor.ProgramSourceCursor[source.Id] = 0;
            }
        }
    }

    private ChannelProgramSource PickFairestSource(
        Channel channel,
        IReadOnlyList<ChannelProgramSource> sources,
        IReadOnlyDictionary<Guid, double> airtimeSeconds,
        DateTime cursor,
        int sequence)
    {
        var minNormalized = sources.Min(s => airtimeSeconds.GetValueOrDefault(s.Id) / Math.Max(0.001, s.TargetAirtimePercent));
        var tied = sources
            .Where(s => Math.Abs((airtimeSeconds.GetValueOrDefault(s.Id) / Math.Max(0.001, s.TargetAirtimePercent)) - minNormalized) < 0.0001)
            .ToList();
        if (tied.Count == 1)
        {
            return tied[0];
        }

        var rng = CreateDeterministicRandom(channel, cursor, sequence, 17);
        return tied[rng.Next(tied.Count)];
    }

    private ResolvedProgramItem? ResolveProgramItem(
        Channel channel,
        ChannelProgramSource source,
        PlayoutAnchorState anchor,
        DateTime cursor,
        int sequence)
    {
        var item = _libraryManager.GetItemById(source.JellyfinItemId);
        if (item is null)
        {
            return null;
        }

        if (item is Series || item is Season)
        {
            var episodes = GetEpisodes(item);
            if (episodes.Count == 0)
            {
                return null;
            }

            Episode episode;
            if (source.PlaybackMode == ProgramPlaybackMode.Random)
            {
                var rng = CreateDeterministicRandom(channel, cursor, sequence, source.Id.GetHashCode());
                episode = episodes[rng.Next(episodes.Count)];
            }
            else
            {
                anchor.ProgramSourceCursor.TryGetValue(source.Id, out var index);
                if (index < 0 || index >= episodes.Count)
                {
                    index = 0;
                }

                episode = episodes[index];
                anchor.ProgramSourceCursor[source.Id] = (index + 1) % episodes.Count;
            }

            return MapProgramItem(episode);
        }

        return MapProgramItem(item);
    }

    private List<Episode> GetEpisodes(BaseItem parent)
    {
        return _libraryManager.GetItemsResult(new InternalItemsQuery
        {
            ParentId = parent.Id,
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            OrderBy = new[]
            {
                (ItemSortBy.ParentIndexNumber, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending),
                (ItemSortBy.IndexNumber, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending),
                (ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending)
            }
        }).Items.OfType<Episode>().ToList();
    }

    private ResolvedProgramItem MapProgramItem(BaseItem item)
    {
        var duration = item.RunTimeTicks.HasValue
            ? TimeSpan.FromTicks(item.RunTimeTicks.Value)
            : TimeSpan.FromMinutes(30);
        var title = item.Name;
        if (item is Episode episode)
        {
            var series = episode.SeriesId == Guid.Empty ? null : _libraryManager.GetItemById(episode.SeriesId);
            var season = episode.ParentIndexNumber.HasValue ? $"S{episode.ParentIndexNumber.Value:00}" : string.Empty;
            var number = episode.IndexNumber.HasValue ? $"E{episode.IndexNumber.Value:00}" : string.Empty;
            var code = season + number;
            if (series is not null)
            {
                title = string.IsNullOrWhiteSpace(code)
                    ? $"{series.Name} · {episode.Name}"
                    : $"{series.Name} · {code} · {episode.Name}";
            }
        }

        return new ResolvedProgramItem(item, title, duration);
    }

    private DateTime AddFillers(
        Channel channel,
        ChannelProgrammingSettings settings,
        IReadOnlyList<ChannelFillerSource> fillers,
        PlayoutAnchorState anchor,
        DateTime cursor,
        int sequence)
    {
        var rng = CreateDeterministicRandom(channel, cursor, sequence, 31);
        if (rng.Next(100) >= settings.FillerChancePercent)
        {
            return cursor;
        }

        var min = Math.Clamp(settings.MinFillerItems, 0, 10);
        var max = Math.Clamp(settings.MaxFillerItems, min, 10);
        var count = max == min ? min : rng.Next(min, max + 1);
        var maxSeconds = Math.Max(0, settings.MaxFillerSeconds);
        var usedSeconds = 0d;

        for (var i = 0; i < count; i++)
        {
            var eligible = fillers
                .Where(f => !anchor.RecentFillerSourceIds.Contains(f.Id))
                .ToList();
            if (eligible.Count == 0)
            {
                eligible = fillers.ToList();
            }

            var filler = PickWeightedFiller(eligible, rng);
            if (filler is null)
            {
                break;
            }

            var item = _libraryManager.GetItemById(filler.JellyfinItemId);
            if (item is null)
            {
                continue;
            }

            var duration = item.RunTimeTicks.HasValue
                ? TimeSpan.FromTicks(item.RunTimeTicks.Value)
                : TimeSpan.FromSeconds(30);
            if (duration <= TimeSpan.Zero)
            {
                continue;
            }

            if (maxSeconds > 0 && usedSeconds + duration.TotalSeconds > maxSeconds)
            {
                continue;
            }

            var finish = cursor.Add(duration);
            _db.PlayoutItems.Add(new PlayoutItem
            {
                ChannelId = channel.Id,
                JellyfinItemId = item.Id,
                Start = cursor,
                Finish = finish,
                Title = item.Name,
                FillerKind = MapFillerKind(filler.Kind),
                GuideGroup = filler.Kind.ToString().ToLowerInvariant()
            });

            cursor = finish;
            usedSeconds += duration.TotalSeconds;
            anchor.RecentFillerSourceIds.Add(filler.Id);
            var keep = Math.Clamp(settings.FillerRepeatWindow, 0, 100);
            while (anchor.RecentFillerSourceIds.Count > keep && anchor.RecentFillerSourceIds.Count > 0)
            {
                anchor.RecentFillerSourceIds.RemoveAt(0);
            }
        }

        return cursor;
    }

    private static ChannelFillerSource? PickWeightedFiller(IReadOnlyList<ChannelFillerSource> fillers, Random rng)
    {
        if (fillers.Count == 0)
        {
            return null;
        }

        var total = fillers.Sum(f => Math.Max(1, f.Weight));
        var roll = rng.Next(total);
        foreach (var filler in fillers)
        {
            roll -= Math.Max(1, filler.Weight);
            if (roll < 0)
            {
                return filler;
            }
        }

        return fillers[^1];
    }

    private static FillerKind MapFillerKind(FillerContentKind kind)
    {
        return kind switch
        {
            FillerContentKind.Bumper => FillerKind.Bumper,
            FillerContentKind.Commercial => FillerKind.Commercial,
            FillerContentKind.StationId => FillerKind.StationId,
            _ => FillerKind.Promo
        };
    }

    private static Random CreateDeterministicRandom(Channel channel, DateTime cursor, int sequence, int salt)
    {
        return new Random(HashCode.Combine(channel.PlayoutSeed, cursor.Date.GetHashCode(), sequence, salt));
    }

    private sealed record ResolvedProgramItem(BaseItem Item, string Title, TimeSpan Duration);
}
