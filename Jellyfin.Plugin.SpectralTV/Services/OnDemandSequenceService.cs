using System.Text.Json;
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
/// Generates smart on-demand channel sequences while keeping progress independent for each progress key.
/// A future client surface should use the logged-in Jellyfin user id as the progress key.
/// </summary>
public class OnDemandSequenceService
{
    private readonly SpectralTvDbContext _db;
    private readonly ILibraryManager _libraryManager;

    public OnDemandSequenceService(SpectralTvDbContext db, ILibraryManager libraryManager)
    {
        _db = db;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Returns a non-destructive preview of the next main programs and interstitials.
    /// </summary>
    public async Task<OnDemandQueueResult> PreviewAsync(
        Guid channelId,
        int programCount,
        string? progressKey = null,
        CancellationToken cancellationToken = default)
    {
        var context = await LoadContextAsync(channelId, cancellationToken);
        var progress = string.IsNullOrWhiteSpace(progressKey)
            ? NewProgress(channelId, "preview")
            : await LoadProgressAsync(channelId, progressKey.Trim(), cancellationToken) ?? NewProgress(channelId, progressKey.Trim());

        var working = CloneProgress(progress);
        var items = new List<OnDemandQueueItem>();
        var remaining = Math.Clamp(programCount, 1, 100);

        if (working.CurrentItemId.HasValue)
        {
            var current = ResolveCurrentProgram(working);
            if (current is not null)
            {
                items.Add(current);
                remaining--;
            }

            CompleteCurrent(working);
        }

        while (remaining > 0)
        {
            var segment = BuildNextSegment(context, working);
            if (segment.Count == 0)
            {
                break;
            }

            items.AddRange(segment);
            remaining--;
            CompleteCurrent(working);
        }

        return new OnDemandQueueResult
        {
            ChannelId = context.Channel.Id,
            ChannelName = context.Channel.Name,
            ProgressKey = progress.ProgressKey,
            IsPreview = true,
            Items = items,
            Progress = MapProgress(working)
        };
    }

    /// <summary>
    /// Returns the user's currently active main program, or selects and persists the next segment.
    /// Set completeCurrent=true only after the client confirms the current main program completed.
    /// Promos are deliberately not progress-bearing items.
    /// </summary>
    public async Task<OnDemandQueueResult> GetNextAsync(
        Guid channelId,
        string progressKey,
        bool completeCurrent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(progressKey))
        {
            throw new ArgumentException("A progress key is required.", nameof(progressKey));
        }

        var context = await LoadContextAsync(channelId, cancellationToken);
        var progress = await LoadProgressAsync(channelId, progressKey.Trim(), cancellationToken)
            ?? NewProgress(channelId, progressKey.Trim());

        if (completeCurrent)
        {
            CompleteCurrent(progress);
        }

        List<OnDemandQueueItem> items;
        if (progress.CurrentItemId.HasValue)
        {
            var current = ResolveCurrentProgram(progress);
            items = current is null ? new List<OnDemandQueueItem>() : new List<OnDemandQueueItem> { current };
        }
        else
        {
            items = BuildNextSegment(context, progress);
        }

        await SaveProgressAsync(progress, cancellationToken);
        return new OnDemandQueueResult
        {
            ChannelId = context.Channel.Id,
            ChannelName = context.Channel.Name,
            ProgressKey = progress.ProgressKey,
            IsPreview = false,
            Items = items,
            Progress = MapProgress(progress)
        };
    }

    public async Task<bool> ReportPositionAsync(
        Guid channelId,
        string progressKey,
        Guid itemId,
        long positionTicks,
        CancellationToken cancellationToken = default)
    {
        var progress = await _db.OnDemandProgress
            .FirstOrDefaultAsync(p => p.ChannelId == channelId && p.ProgressKey == progressKey, cancellationToken);
        if (progress?.CurrentItemId != itemId)
        {
            return false;
        }

        progress.CurrentPositionTicks = Math.Max(0, positionTicks);
        progress.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task ResetProgressAsync(Guid channelId, string progressKey, CancellationToken cancellationToken = default)
    {
        var rows = await _db.OnDemandProgress
            .Where(p => p.ChannelId == channelId && p.ProgressKey == progressKey)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return;
        }

        _db.OnDemandProgress.RemoveRange(rows);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<OnDemandContext> LoadContextAsync(Guid channelId, CancellationToken cancellationToken)
    {
        var channel = await _db.OnDemandChannels
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken)
            ?? throw new InvalidOperationException("On-demand channel not found.");
        if (!channel.Enabled)
        {
            throw new InvalidOperationException("This on-demand channel is disabled.");
        }

        var sources = await _db.OnDemandSources
            .AsNoTracking()
            .Where(s => s.ChannelId == channelId && s.Enabled)
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);
        if (sources.Count == 0)
        {
            throw new InvalidOperationException("This on-demand channel has no enabled programming sources.");
        }

        var fillers = channel.FillerEnabled
            ? await _db.OnDemandFillerSources
                .AsNoTracking()
                .Where(f => f.ChannelId == channelId && f.Enabled && f.Weight > 0)
                .OrderBy(f => f.SortOrder)
                .ThenBy(f => f.Id)
                .ToListAsync(cancellationToken)
            : new List<OnDemandFillerSource>();

        return new OnDemandContext(channel, sources, fillers);
    }

    private async Task<OnDemandProgress?> LoadProgressAsync(Guid channelId, string progressKey, CancellationToken cancellationToken)
    {
        return await _db.OnDemandProgress
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.ChannelId == channelId && p.ProgressKey == progressKey, cancellationToken);
    }

    private static OnDemandProgress NewProgress(Guid channelId, string progressKey)
        => new()
        {
            ChannelId = channelId,
            ProgressKey = progressKey,
            UpdatedAt = DateTime.UtcNow
        };

    private List<OnDemandQueueItem> BuildNextSegment(OnDemandContext context, OnDemandProgress progress)
    {
        var sourceCursors = ReadDictionary(progress.SourceCursorJson);
        var pickCounts = ReadDictionary(progress.SourcePickCountJson);
        var shuffleBag = ReadGuidList(progress.ShuffleBagJson);
        var recentFillers = ReadGuidList(progress.RecentFillerJson);

        var isFirstProgram = progress.PatternIndex == 0
            && progress.LastSourceId is null
            && progress.CurrentItemId is null;
        var previousSourceId = progress.LastSourceId;
        var source = SelectSource(context, progress, sourceCursors, pickCounts, shuffleBag);
        var program = ResolveProgram(source, sourceCursors, progress.PatternIndex);
        if (program is null)
        {
            // Missing/empty sources should not poison progress. Try other enabled sources once.
            foreach (var fallback in context.Sources.Where(s => s.Id != source.Id))
            {
                program = ResolveProgram(fallback, sourceCursors, progress.PatternIndex);
                if (program is not null)
                {
                    source = fallback;
                    break;
                }
            }
        }

        if (program is null)
        {
            return new List<OnDemandQueueItem>();
        }

        pickCounts[source.Id] = pickCounts.GetValueOrDefault(source.Id) + 1;
        progress.PatternIndex++;
        progress.CurrentSourceId = source.Id;
        progress.CurrentItemId = program.Item.Id;
        progress.CurrentPositionTicks = 0;
        progress.SourceCursorJson = WriteDictionary(sourceCursors);
        progress.SourcePickCountJson = WriteDictionary(pickCounts);
        progress.ShuffleBagJson = WriteGuidList(shuffleBag);

        var segment = new List<OnDemandQueueItem>();
        var sourceChanged = previousSourceId.HasValue && previousSourceId.Value != source.Id;
        var shouldInsertFiller = context.Fillers.Count > 0
            && context.Channel.FillerEnabled
            && ((isFirstProgram && context.Channel.FillerBeforeFirstProgram)
                || (!isFirstProgram
                    && context.Channel.FillerBetweenPrograms
                    && (!context.Channel.FillerOnSourceChangeOnly || sourceChanged)));

        if (shouldInsertFiller)
        {
            var rng = CreateRandom(context.Channel.Id, progress.PatternIndex, 71);
            AddFillers(context.Channel, context.Fillers, recentFillers, segment, rng);
        }

        progress.RecentFillerJson = WriteGuidList(recentFillers);
        progress.UpdatedAt = DateTime.UtcNow;
        segment.Add(MapProgram(program, source, 0));
        return segment;
    }

    private OnDemandSource SelectSource(
        OnDemandContext context,
        OnDemandProgress progress,
        Dictionary<Guid, int> sourceCursors,
        Dictionary<Guid, int> pickCounts,
        List<Guid> shuffleBag)
    {
        var sources = context.Sources;
        if (context.Channel.ProgrammingMode == OnDemandProgrammingMode.FixedSequence)
        {
            return sources[NormalizeIndex(progress.PatternIndex, sources.Count)];
        }

        return context.Channel.RotationMode switch
        {
            OnDemandRotationMode.Blocks => SelectBlockSource(sources, progress.PatternIndex),
            OnDemandRotationMode.BalancedRandom => SelectBalancedRandom(context.Channel.Id, sources, pickCounts, progress.PatternIndex),
            OnDemandRotationMode.WeightedRandom => SelectWeightedRandom(context.Channel.Id, sources, progress.PatternIndex),
            OnDemandRotationMode.Random => sources[CreateRandom(context.Channel.Id, progress.PatternIndex, 13).Next(sources.Count)],
            OnDemandRotationMode.ShuffleCycle => SelectShuffleSource(context.Channel.Id, sources, shuffleBag, progress.PatternIndex),
            OnDemandRotationMode.CustomPattern => SelectCustomPatternSource(context.Channel, sources, progress.PatternIndex),
            _ => sources[NormalizeIndex(progress.PatternIndex, sources.Count)]
        };
    }

    private static OnDemandSource SelectBlockSource(IReadOnlyList<OnDemandSource> sources, int patternIndex)
    {
        var cycleLength = sources.Sum(s => Math.Max(1, s.BlockSize));
        var offset = NormalizeIndex(patternIndex, cycleLength);
        foreach (var source in sources)
        {
            var size = Math.Max(1, source.BlockSize);
            if (offset < size)
            {
                return source;
            }

            offset -= size;
        }

        return sources[^1];
    }

    private static OnDemandSource SelectBalancedRandom(
        Guid channelId,
        IReadOnlyList<OnDemandSource> sources,
        IReadOnlyDictionary<Guid, int> pickCounts,
        int patternIndex)
    {
        var min = sources.Min(s => pickCounts.GetValueOrDefault(s.Id));
        var tied = sources.Where(s => pickCounts.GetValueOrDefault(s.Id) == min).ToList();
        var rng = CreateRandom(channelId, patternIndex, 23);
        return tied[rng.Next(tied.Count)];
    }

    private static OnDemandSource SelectWeightedRandom(Guid channelId, IReadOnlyList<OnDemandSource> sources, int patternIndex)
    {
        var total = sources.Sum(s => Math.Max(1, s.Weight));
        var roll = CreateRandom(channelId, patternIndex, 31).Next(total);
        foreach (var source in sources)
        {
            roll -= Math.Max(1, source.Weight);
            if (roll < 0)
            {
                return source;
            }
        }

        return sources[^1];
    }

    private static OnDemandSource SelectShuffleSource(
        Guid channelId,
        IReadOnlyList<OnDemandSource> sources,
        List<Guid> shuffleBag,
        int patternIndex)
    {
        var validIds = sources.Select(s => s.Id).ToHashSet();
        shuffleBag.RemoveAll(id => !validIds.Contains(id));
        if (shuffleBag.Count == 0)
        {
            shuffleBag.AddRange(sources.Select(s => s.Id));
            var rng = CreateRandom(channelId, patternIndex, 43);
            for (var i = shuffleBag.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (shuffleBag[i], shuffleBag[j]) = (shuffleBag[j], shuffleBag[i]);
            }
        }

        var nextId = shuffleBag[0];
        shuffleBag.RemoveAt(0);
        return sources.First(s => s.Id == nextId);
    }

    private static OnDemandSource SelectCustomPatternSource(
        OnDemandChannel channel,
        IReadOnlyList<OnDemandSource> sources,
        int patternIndex)
    {
        var valid = sources.ToDictionary(s => s.Id);
        var pattern = ReadGuidList(channel.CustomPatternJson).Where(valid.ContainsKey).ToList();
        if (pattern.Count == 0)
        {
            return sources[NormalizeIndex(patternIndex, sources.Count)];
        }

        return valid[pattern[NormalizeIndex(patternIndex, pattern.Count)]];
    }

    private ResolvedProgram? ResolveProgram(OnDemandSource source, Dictionary<Guid, int> sourceCursors, int patternIndex)
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
                episode = episodes[CreateRandom(source.Id, patternIndex, 59).Next(episodes.Count)];
            }
            else
            {
                var index = sourceCursors.GetValueOrDefault(source.Id);
                index = NormalizeIndex(index, episodes.Count);
                episode = episodes[index];
                sourceCursors[source.Id] = (index + 1) % episodes.Count;
            }

            return MapProgram(episode);
        }

        return MapProgram(item);
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

    private ResolvedProgram MapProgram(BaseItem item)
    {
        var title = item.Name;
        string? sourceName = null;
        if (item is Episode episode)
        {
            var series = episode.SeriesId == Guid.Empty ? null : _libraryManager.GetItemById(episode.SeriesId);
            sourceName = series?.Name;
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

        return new ResolvedProgram(item, title, sourceName);
    }

    private OnDemandQueueItem? ResolveCurrentProgram(OnDemandProgress progress)
    {
        if (!progress.CurrentItemId.HasValue)
        {
            return null;
        }

        var item = _libraryManager.GetItemById(progress.CurrentItemId.Value);
        if (item is null)
        {
            return null;
        }

        var mapped = MapProgram(item);
        return new OnDemandQueueItem
        {
            Kind = "program",
            JellyfinItemId = item.Id,
            SourceId = progress.CurrentSourceId,
            Title = mapped.Title,
            SourceName = mapped.SourceName,
            ResumePositionTicks = Math.Max(0, progress.CurrentPositionTicks)
        };
    }

    private static OnDemandQueueItem MapProgram(ResolvedProgram program, OnDemandSource source, long resumePositionTicks)
        => new()
        {
            Kind = "program",
            JellyfinItemId = program.Item.Id,
            SourceId = source.Id,
            Title = program.Title,
            SourceName = program.SourceName,
            ResumePositionTicks = Math.Max(0, resumePositionTicks)
        };

    private void AddFillers(
        OnDemandChannel channel,
        IReadOnlyList<OnDemandFillerSource> fillers,
        List<Guid> recentFillers,
        List<OnDemandQueueItem> output,
        Random rng)
    {
        var min = Math.Clamp(channel.MinFillerItems, 0, 10);
        var max = Math.Clamp(channel.MaxFillerItems, min, 10);
        var count = min == max ? min : rng.Next(min, max + 1);
        for (var i = 0; i < count; i++)
        {
            var eligible = fillers.Where(f => !recentFillers.Contains(f.Id)).ToList();
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

            output.Add(new OnDemandQueueItem
            {
                Kind = filler.Kind.ToString().ToLowerInvariant(),
                JellyfinItemId = item.Id,
                FillerSourceId = filler.Id,
                Title = item.Name,
                ResumePositionTicks = 0
            });

            recentFillers.Add(filler.Id);
            var keep = Math.Clamp(channel.FillerRepeatWindow, 0, 100);
            while (recentFillers.Count > keep && recentFillers.Count > 0)
            {
                recentFillers.RemoveAt(0);
            }
        }
    }

    private static OnDemandFillerSource? PickWeightedFiller(IReadOnlyList<OnDemandFillerSource> fillers, Random rng)
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

    private static void CompleteCurrent(OnDemandProgress progress)
    {
        if (progress.CurrentSourceId.HasValue)
        {
            progress.LastSourceId = progress.CurrentSourceId;
        }

        progress.CurrentSourceId = null;
        progress.CurrentItemId = null;
        progress.CurrentPositionTicks = 0;
        progress.UpdatedAt = DateTime.UtcNow;
    }

    private async Task SaveProgressAsync(OnDemandProgress progress, CancellationToken cancellationToken)
    {
        var row = await _db.OnDemandProgress
            .FirstOrDefaultAsync(p => p.ChannelId == progress.ChannelId && p.ProgressKey == progress.ProgressKey, cancellationToken);
        if (row is null)
        {
            row = CloneProgress(progress);
            row.Id = Guid.NewGuid();
            _db.OnDemandProgress.Add(row);
        }
        else
        {
            row.PatternIndex = progress.PatternIndex;
            row.LastSourceId = progress.LastSourceId;
            row.CurrentSourceId = progress.CurrentSourceId;
            row.SourceCursorJson = progress.SourceCursorJson;
            row.SourcePickCountJson = progress.SourcePickCountJson;
            row.ShuffleBagJson = progress.ShuffleBagJson;
            row.RecentFillerJson = progress.RecentFillerJson;
            row.CurrentItemId = progress.CurrentItemId;
            row.CurrentPositionTicks = progress.CurrentPositionTicks;
            row.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static OnDemandProgress CloneProgress(OnDemandProgress source)
        => new()
        {
            Id = source.Id,
            ChannelId = source.ChannelId,
            ProgressKey = source.ProgressKey,
            PatternIndex = source.PatternIndex,
            LastSourceId = source.LastSourceId,
            CurrentSourceId = source.CurrentSourceId,
            SourceCursorJson = source.SourceCursorJson,
            SourcePickCountJson = source.SourcePickCountJson,
            ShuffleBagJson = source.ShuffleBagJson,
            RecentFillerJson = source.RecentFillerJson,
            CurrentItemId = source.CurrentItemId,
            CurrentPositionTicks = source.CurrentPositionTicks,
            UpdatedAt = source.UpdatedAt
        };

    private static OnDemandProgressDto MapProgress(OnDemandProgress progress)
        => new()
        {
            PatternIndex = progress.PatternIndex,
            LastSourceId = progress.LastSourceId,
            CurrentSourceId = progress.CurrentSourceId,
            CurrentItemId = progress.CurrentItemId,
            CurrentPositionTicks = progress.CurrentPositionTicks,
            UpdatedAt = progress.UpdatedAt
        };

    private static Dictionary<Guid, int> ReadDictionary(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json)
                ? new Dictionary<Guid, int>()
                : JsonSerializer.Deserialize<Dictionary<Guid, int>>(json) ?? new Dictionary<Guid, int>();
        }
        catch
        {
            return new Dictionary<Guid, int>();
        }
    }

    private static List<Guid> ReadGuidList(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json)
                ? new List<Guid>()
                : JsonSerializer.Deserialize<List<Guid>>(json) ?? new List<Guid>();
        }
        catch
        {
            return new List<Guid>();
        }
    }

    private static string WriteDictionary(Dictionary<Guid, int> value) => JsonSerializer.Serialize(value);

    private static string WriteGuidList(List<Guid> value) => JsonSerializer.Serialize(value);

    private static int NormalizeIndex(int value, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        var result = value % count;
        return result < 0 ? result + count : result;
    }

    private static Random CreateRandom(Guid seedId, int index, int salt)
        => new(HashCode.Combine(seedId, index, salt));

    private sealed record OnDemandContext(
        OnDemandChannel Channel,
        IReadOnlyList<OnDemandSource> Sources,
        IReadOnlyList<OnDemandFillerSource> Fillers);

    private sealed record ResolvedProgram(BaseItem Item, string Title, string? SourceName);
}

public class OnDemandQueueResult
{
    public Guid ChannelId { get; set; }
    public string ChannelName { get; set; } = string.Empty;
    public string ProgressKey { get; set; } = string.Empty;
    public bool IsPreview { get; set; }
    public List<OnDemandQueueItem> Items { get; set; } = new();
    public OnDemandProgressDto Progress { get; set; } = new();
}

public class OnDemandQueueItem
{
    public string Kind { get; set; } = "program";
    public Guid JellyfinItemId { get; set; }
    public Guid? SourceId { get; set; }
    public Guid? FillerSourceId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? SourceName { get; set; }
    public long ResumePositionTicks { get; set; }
}

public class OnDemandProgressDto
{
    public int PatternIndex { get; set; }
    public Guid? LastSourceId { get; set; }
    public Guid? CurrentSourceId { get; set; }
    public Guid? CurrentItemId { get; set; }
    public long CurrentPositionTicks { get; set; }
    public DateTime UpdatedAt { get; set; }
}
