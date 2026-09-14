using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.FinTV.Api;

/// <summary>
/// Configuration API for continuous weighted virtual-TV channels.
/// </summary>
[ApiController]
[Route("FinTV/api/programming")]
[Authorize(Policy = Policies.RequiresElevation)]
public class ProgrammingController : ControllerBase
{
    private readonly FinTvDbContext _db;
    private readonly ILibraryManager _libraryManager;
    private readonly WeightedProgrammingService _programming;
    private readonly PlayoutBuilderService _playoutBuilder;

    public ProgrammingController(
        FinTvDbContext db,
        ILibraryManager libraryManager,
        WeightedProgrammingService programming,
        PlayoutBuilderService playoutBuilder)
    {
        _db = db;
        _libraryManager = libraryManager;
        _programming = programming;
        _playoutBuilder = playoutBuilder;
    }

    [HttpGet("{channelId:guid}")]
    public async Task<IActionResult> Get(Guid channelId, CancellationToken cancellationToken)
    {
        var channel = await _db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        var settings = await _programming.GetSettingsAsync(channelId, cancellationToken);
        var sources = await _db.ChannelProgramSources
            .AsNoTracking()
            .Where(s => s.ChannelId == channelId)
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);
        var fillers = await _db.ChannelFillerSources
            .AsNoTracking()
            .Where(s => s.ChannelId == channelId)
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            channel = new { channel.Id, channel.Number, channel.Name },
            settings,
            totalTargetAirtime = sources.Where(s => s.Enabled).Sum(s => s.TargetAirtimePercent),
            sources = sources.Select(MapProgramSource),
            fillers = fillers.Select(MapFillerSource),
            rebuild = _playoutBuilder.GetRebuildState(channelId)
        });
    }

    [HttpPut("{channelId:guid}/settings")]
    public async Task<IActionResult> SaveSettings(
        Guid channelId,
        [FromBody] ChannelProgrammingSettings request,
        CancellationToken cancellationToken)
    {
        if (!await _db.Channels.AnyAsync(c => c.Id == channelId, cancellationToken))
        {
            return NotFound();
        }

        var saved = await _programming.SaveSettingsAsync(channelId, request, cancellationToken);
        return Ok(saved);
    }

    [HttpPost("{channelId:guid}/sources")]
    public async Task<IActionResult> AddSource(
        Guid channelId,
        [FromBody] ProgramSourceRequest request,
        CancellationToken cancellationToken)
    {
        if (!await _db.Channels.AnyAsync(c => c.Id == channelId, cancellationToken))
        {
            return NotFound();
        }

        var item = _libraryManager.GetItemById(request.JellyfinItemId);
        if (item is null)
        {
            return BadRequest(new { message = "The selected Jellyfin item no longer exists." });
        }

        if (!IsSupportedProgramItem(item))
        {
            return BadRequest(new { message = "Programming sources must be a series, season, episode, movie, or music video." });
        }

        if (await _db.ChannelProgramSources.AnyAsync(
            s => s.ChannelId == channelId && s.JellyfinItemId == request.JellyfinItemId,
            cancellationToken))
        {
            return BadRequest(new { message = "That item is already in this channel's programming pool." });
        }

        var nextOrder = (await _db.ChannelProgramSources
            .Where(s => s.ChannelId == channelId)
            .Select(s => (int?)s.SortOrder)
            .MaxAsync(cancellationToken) ?? -1) + 1;
        var source = new ChannelProgramSource
        {
            ChannelId = channelId,
            JellyfinItemId = request.JellyfinItemId,
            TargetAirtimePercent = ClampAirtime(request.TargetAirtimePercent),
            PlaybackMode = request.PlaybackMode,
            Enabled = request.Enabled,
            SortOrder = nextOrder
        };
        _db.ChannelProgramSources.Add(source);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(MapProgramSource(source));
    }

    [HttpPut("sources/{sourceId:guid}")]
    public async Task<IActionResult> UpdateSource(
        Guid sourceId,
        [FromBody] ProgramSourceUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var source = await _db.ChannelProgramSources.FirstOrDefaultAsync(s => s.Id == sourceId, cancellationToken);
        if (source is null)
        {
            return NotFound();
        }

        source.TargetAirtimePercent = ClampAirtime(request.TargetAirtimePercent);
        source.PlaybackMode = request.PlaybackMode;
        source.Enabled = request.Enabled;
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(MapProgramSource(source));
    }

    [HttpDelete("sources/{sourceId:guid}")]
    public async Task<IActionResult> DeleteSource(Guid sourceId, CancellationToken cancellationToken)
    {
        var source = await _db.ChannelProgramSources.FirstOrDefaultAsync(s => s.Id == sourceId, cancellationToken);
        if (source is null)
        {
            return NotFound();
        }

        _db.ChannelProgramSources.Remove(source);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{channelId:guid}/fillers")]
    public async Task<IActionResult> AddFiller(
        Guid channelId,
        [FromBody] FillerSourceRequest request,
        CancellationToken cancellationToken)
    {
        if (!await _db.Channels.AnyAsync(c => c.Id == channelId, cancellationToken))
        {
            return NotFound();
        }

        var item = _libraryManager.GetItemById(request.JellyfinItemId);
        if (item is null)
        {
            return BadRequest(new { message = "The selected Jellyfin item no longer exists." });
        }

        if (item is Series || item is Season)
        {
            return BadRequest(new { message = "Promos and bumpers must be individual playable media items." });
        }

        var nextOrder = (await _db.ChannelFillerSources
            .Where(s => s.ChannelId == channelId)
            .Select(s => (int?)s.SortOrder)
            .MaxAsync(cancellationToken) ?? -1) + 1;
        var source = new ChannelFillerSource
        {
            ChannelId = channelId,
            JellyfinItemId = request.JellyfinItemId,
            Kind = request.Kind,
            Weight = Math.Clamp(request.Weight, 1, 1000),
            Enabled = request.Enabled,
            SortOrder = nextOrder
        };
        _db.ChannelFillerSources.Add(source);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(MapFillerSource(source));
    }

    [HttpPut("fillers/{sourceId:guid}")]
    public async Task<IActionResult> UpdateFiller(
        Guid sourceId,
        [FromBody] FillerSourceUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var source = await _db.ChannelFillerSources.FirstOrDefaultAsync(s => s.Id == sourceId, cancellationToken);
        if (source is null)
        {
            return NotFound();
        }

        source.Kind = request.Kind;
        source.Weight = Math.Clamp(request.Weight, 1, 1000);
        source.Enabled = request.Enabled;
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(MapFillerSource(source));
    }

    [HttpDelete("fillers/{sourceId:guid}")]
    public async Task<IActionResult> DeleteFiller(Guid sourceId, CancellationToken cancellationToken)
    {
        var source = await _db.ChannelFillerSources.FirstOrDefaultAsync(s => s.Id == sourceId, cancellationToken);
        if (source is null)
        {
            return NotFound();
        }

        _db.ChannelFillerSources.Remove(source);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{channelId:guid}/rebuild")]
    public IActionResult Rebuild(Guid channelId)
    {
        _playoutBuilder.QueueRebuildChannel(channelId);
        return Accepted(new { queued = true });
    }

    [HttpGet("{channelId:guid}/rebuild/status")]
    public IActionResult RebuildStatus(Guid channelId)
        => Ok(_playoutBuilder.GetRebuildState(channelId) ?? new ChannelPlayoutRebuildState { State = "idle" });

    private object MapProgramSource(ChannelProgramSource source)
    {
        var item = _libraryManager.GetItemById(source.JellyfinItemId);
        return new
        {
            source.Id,
            source.ChannelId,
            source.JellyfinItemId,
            source.TargetAirtimePercent,
            source.PlaybackMode,
            source.Enabled,
            source.SortOrder,
            name = item?.Name ?? "Missing item",
            type = item?.GetBaseItemKind().ToString() ?? "Missing",
            runtimeMinutes = GetRuntimeMinutes(item)
        };
    }

    private object MapFillerSource(ChannelFillerSource source)
    {
        var item = _libraryManager.GetItemById(source.JellyfinItemId);
        return new
        {
            source.Id,
            source.ChannelId,
            source.JellyfinItemId,
            source.Kind,
            source.Weight,
            source.Enabled,
            source.SortOrder,
            name = item?.Name ?? "Missing item",
            type = item?.GetBaseItemKind().ToString() ?? "Missing",
            runtimeSeconds = item?.RunTimeTicks is long ticks ? (int)Math.Round(TimeSpan.FromTicks(ticks).TotalSeconds) : (int?)null
        };
    }

    private static bool IsSupportedProgramItem(BaseItem item)
        => item is Series or Season or Episode or Movie || item.GetBaseItemKind().ToString() == "MusicVideo";

    private static int? GetRuntimeMinutes(BaseItem? item)
        => item?.RunTimeTicks is long ticks ? (int)Math.Round(TimeSpan.FromTicks(ticks).TotalMinutes) : null;

    private static double ClampAirtime(double value)
        => Math.Clamp(double.IsFinite(value) ? value : 1, 0.1, 10000);
}

public class ProgramSourceRequest
{
    public Guid JellyfinItemId { get; set; }

    public double TargetAirtimePercent { get; set; } = 100;

    public ProgramPlaybackMode PlaybackMode { get; set; } = ProgramPlaybackMode.Sequential;

    public bool Enabled { get; set; } = true;
}

public class ProgramSourceUpdateRequest
{
    public double TargetAirtimePercent { get; set; } = 100;

    public ProgramPlaybackMode PlaybackMode { get; set; } = ProgramPlaybackMode.Sequential;

    public bool Enabled { get; set; } = true;
}

public class FillerSourceRequest
{
    public Guid JellyfinItemId { get; set; }

    public FillerContentKind Kind { get; set; } = FillerContentKind.Promo;

    public int Weight { get; set; } = 1;

    public bool Enabled { get; set; } = true;
}

public class FillerSourceUpdateRequest
{
    public FillerContentKind Kind { get; set; } = FillerContentKind.Promo;

    public int Weight { get; set; } = 1;

    public bool Enabled { get; set; } = true;
}
