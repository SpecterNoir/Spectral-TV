using Jellyfin.Plugin.SpectralTV.Data;
using Jellyfin.Plugin.SpectralTV.Domain;
using Jellyfin.Plugin.SpectralTV.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.SpectralTV.Api;

/// <summary>
/// Admin API for smart on-demand channel recipes. The queue/progress endpoints are intentionally
/// elevation-only in this first build; a viewer-facing surface will bind progress to the authenticated
/// Jellyfin user rather than accepting arbitrary progress keys.
/// </summary>
[ApiController]
[Route("SpectralTV/api/on-demand")]
[Authorize(Policy = Policies.RequiresElevation)]
public class OnDemandController : ControllerBase
{
    private readonly SpectralTvDbContext _db;
    private readonly ILibraryManager _libraryManager;
    private readonly OnDemandSequenceService _sequence;

    public OnDemandController(
        SpectralTvDbContext db,
        ILibraryManager libraryManager,
        OnDemandSequenceService sequence)
    {
        _db = db;
        _libraryManager = libraryManager;
        _sequence = sequence;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<object>>> GetAll(CancellationToken cancellationToken)
    {
        var channels = await _db.OnDemandChannels
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);
        return Ok(channels.Select(c => new
        {
            c.Id,
            c.Name,
            c.Enabled,
            c.ProgrammingMode,
            c.RotationMode,
            c.UpdatedAt
        }));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<object>> Get(Guid id, CancellationToken cancellationToken)
    {
        var channel = await _db.OnDemandChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        var sources = await _db.OnDemandSources
            .AsNoTracking()
            .Where(s => s.ChannelId == id)
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);
        var fillers = await _db.OnDemandFillerSources
            .AsNoTracking()
            .Where(s => s.ChannelId == id)
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            channel,
            sources = sources.Select(MapSource),
            fillers = fillers.Select(MapFiller)
        });
    }

    [HttpPost]
    public async Task<ActionResult<OnDemandChannel>> Create([FromBody] OnDemandChannelRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { message = "Channel name is required." });
        }

        var channel = new OnDemandChannel
        {
            Name = request.Name.Trim(),
            Enabled = request.Enabled,
            ProgrammingMode = request.ProgrammingMode,
            RotationMode = request.RotationMode,
            CustomPatternJson = NormalizePatternJson(request.CustomPatternJson),
            FillerEnabled = request.FillerEnabled,
            FillerBeforeFirstProgram = request.FillerBeforeFirstProgram,
            FillerBetweenPrograms = request.FillerBetweenPrograms,
            FillerOnSourceChangeOnly = request.FillerOnSourceChangeOnly,
            MinFillerItems = Math.Clamp(request.MinFillerItems, 0, 10),
            MaxFillerItems = Math.Clamp(request.MaxFillerItems, Math.Clamp(request.MinFillerItems, 0, 10), 10),
            FillerRepeatWindow = Math.Clamp(request.FillerRepeatWindow, 0, 100),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.OnDemandChannels.Add(channel);
        await _db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = channel.Id }, channel);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<OnDemandChannel>> Update(Guid id, [FromBody] OnDemandChannelRequest request, CancellationToken cancellationToken)
    {
        var channel = await _db.OnDemandChannels.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { message = "Channel name is required." });
        }

        channel.Name = request.Name.Trim();
        channel.Enabled = request.Enabled;
        channel.ProgrammingMode = request.ProgrammingMode;
        channel.RotationMode = request.RotationMode;
        channel.CustomPatternJson = NormalizePatternJson(request.CustomPatternJson);
        channel.FillerEnabled = request.FillerEnabled;
        channel.FillerBeforeFirstProgram = request.FillerBeforeFirstProgram;
        channel.FillerBetweenPrograms = request.FillerBetweenPrograms;
        channel.FillerOnSourceChangeOnly = request.FillerOnSourceChangeOnly;
        channel.MinFillerItems = Math.Clamp(request.MinFillerItems, 0, 10);
        channel.MaxFillerItems = Math.Clamp(request.MaxFillerItems, channel.MinFillerItems, 10);
        channel.FillerRepeatWindow = Math.Clamp(request.FillerRepeatWindow, 0, 100);
        channel.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(channel);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var channel = await _db.OnDemandChannels.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (channel is null)
        {
            return NotFound();
        }

        _db.OnDemandChannels.Remove(channel);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{channelId:guid}/sources")]
    public async Task<ActionResult<object>> AddSource(Guid channelId, [FromBody] OnDemandSourceRequest request, CancellationToken cancellationToken)
    {
        if (!await _db.OnDemandChannels.AnyAsync(c => c.Id == channelId, cancellationToken))
        {
            return NotFound();
        }

        var item = _libraryManager.GetItemById(request.JellyfinItemId);
        if (item is null)
        {
            return BadRequest(new { message = "The selected Jellyfin item no longer exists." });
        }

        if (item is not Series && item is not Season && item is not Episode && item is not Movie)
        {
            return BadRequest(new { message = "On-demand programming sources must be a series, season, episode, or movie." });
        }

        var nextOrder = (await _db.OnDemandSources
            .Where(s => s.ChannelId == channelId)
            .Select(s => (int?)s.SortOrder)
            .MaxAsync(cancellationToken) ?? -1) + 1;
        var source = new OnDemandSource
        {
            ChannelId = channelId,
            JellyfinItemId = request.JellyfinItemId,
            Weight = Math.Clamp(request.Weight, 1, 1000),
            BlockSize = Math.Clamp(request.BlockSize, 1, 100),
            PlaybackMode = request.PlaybackMode,
            Enabled = request.Enabled,
            SortOrder = nextOrder
        };
        _db.OnDemandSources.Add(source);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(MapSource(source));
    }

    [HttpPut("sources/{sourceId:guid}")]
    public async Task<ActionResult<object>> UpdateSource(Guid sourceId, [FromBody] OnDemandSourceUpdateRequest request, CancellationToken cancellationToken)
    {
        var source = await _db.OnDemandSources.FirstOrDefaultAsync(s => s.Id == sourceId, cancellationToken);
        if (source is null)
        {
            return NotFound();
        }

        source.Weight = Math.Clamp(request.Weight, 1, 1000);
        source.BlockSize = Math.Clamp(request.BlockSize, 1, 100);
        source.PlaybackMode = request.PlaybackMode;
        source.Enabled = request.Enabled;
        source.SortOrder = Math.Max(0, request.SortOrder);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(MapSource(source));
    }

    [HttpDelete("sources/{sourceId:guid}")]
    public async Task<IActionResult> DeleteSource(Guid sourceId, CancellationToken cancellationToken)
    {
        var source = await _db.OnDemandSources.FirstOrDefaultAsync(s => s.Id == sourceId, cancellationToken);
        if (source is null)
        {
            return NotFound();
        }

        _db.OnDemandSources.Remove(source);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{channelId:guid}/fillers")]
    public async Task<ActionResult<object>> AddFiller(Guid channelId, [FromBody] OnDemandFillerRequest request, CancellationToken cancellationToken)
    {
        if (!await _db.OnDemandChannels.AnyAsync(c => c.Id == channelId, cancellationToken))
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
            return BadRequest(new { message = "Promos and bumpers must be individual playable items." });
        }

        var nextOrder = (await _db.OnDemandFillerSources
            .Where(s => s.ChannelId == channelId)
            .Select(s => (int?)s.SortOrder)
            .MaxAsync(cancellationToken) ?? -1) + 1;
        var filler = new OnDemandFillerSource
        {
            ChannelId = channelId,
            JellyfinItemId = request.JellyfinItemId,
            Kind = request.Kind,
            Weight = Math.Clamp(request.Weight, 1, 1000),
            Enabled = request.Enabled,
            SortOrder = nextOrder
        };
        _db.OnDemandFillerSources.Add(filler);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(MapFiller(filler));
    }

    [HttpPut("fillers/{fillerId:guid}")]
    public async Task<ActionResult<object>> UpdateFiller(Guid fillerId, [FromBody] OnDemandFillerUpdateRequest request, CancellationToken cancellationToken)
    {
        var filler = await _db.OnDemandFillerSources.FirstOrDefaultAsync(f => f.Id == fillerId, cancellationToken);
        if (filler is null)
        {
            return NotFound();
        }

        filler.Kind = request.Kind;
        filler.Weight = Math.Clamp(request.Weight, 1, 1000);
        filler.Enabled = request.Enabled;
        filler.SortOrder = Math.Max(0, request.SortOrder);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(MapFiller(filler));
    }

    [HttpDelete("fillers/{fillerId:guid}")]
    public async Task<IActionResult> DeleteFiller(Guid fillerId, CancellationToken cancellationToken)
    {
        var filler = await _db.OnDemandFillerSources.FirstOrDefaultAsync(f => f.Id == fillerId, cancellationToken);
        if (filler is null)
        {
            return NotFound();
        }

        _db.OnDemandFillerSources.Remove(filler);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("{channelId:guid}/preview")]
    public async Task<ActionResult<OnDemandQueueResult>> Preview(
        Guid channelId,
        [FromQuery] int count = 12,
        [FromQuery] string? progressKey = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await _sequence.PreviewAsync(channelId, count, progressKey, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{channelId:guid}/next")]
    public async Task<ActionResult<OnDemandQueueResult>> Next(
        Guid channelId,
        [FromBody] OnDemandNextRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _sequence.GetNextAsync(channelId, request.ProgressKey, request.CompleteCurrent, cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{channelId:guid}/position")]
    public async Task<IActionResult> Position(Guid channelId, [FromBody] OnDemandPositionRequest request, CancellationToken cancellationToken)
    {
        var updated = await _sequence.ReportPositionAsync(
            channelId,
            request.ProgressKey,
            request.ItemId,
            request.PositionTicks,
            cancellationToken);
        return updated ? NoContent() : NotFound(new { message = "No matching active on-demand item was found." });
    }

    [HttpDelete("{channelId:guid}/progress")]
    public async Task<IActionResult> ResetProgress(Guid channelId, [FromQuery] string progressKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(progressKey))
        {
            return BadRequest(new { message = "A progress key is required." });
        }

        await _sequence.ResetProgressAsync(channelId, progressKey.Trim(), cancellationToken);
        return NoContent();
    }

    private object MapSource(OnDemandSource source)
    {
        var item = _libraryManager.GetItemById(source.JellyfinItemId);
        return new
        {
            source.Id,
            source.ChannelId,
            source.JellyfinItemId,
            source.Weight,
            source.BlockSize,
            source.PlaybackMode,
            source.Enabled,
            source.SortOrder,
            name = item?.Name ?? "Missing item",
            type = item?.GetBaseItemKind().ToString() ?? "Missing"
        };
    }

    private object MapFiller(OnDemandFillerSource filler)
    {
        var item = _libraryManager.GetItemById(filler.JellyfinItemId);
        return new
        {
            filler.Id,
            filler.ChannelId,
            filler.JellyfinItemId,
            filler.Kind,
            filler.Weight,
            filler.Enabled,
            filler.SortOrder,
            name = item?.Name ?? "Missing item",
            type = item?.GetBaseItemKind().ToString() ?? "Missing"
        };
    }

    private static string NormalizePatternJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "[]";
        }

        try
        {
            var ids = System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(value);
            return System.Text.Json.JsonSerializer.Serialize(ids ?? new List<Guid>());
        }
        catch
        {
            return "[]";
        }
    }
}

public class OnDemandChannelRequest
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public OnDemandProgrammingMode ProgrammingMode { get; set; } = OnDemandProgrammingMode.DynamicSources;
    public OnDemandRotationMode RotationMode { get; set; } = OnDemandRotationMode.Alternating;
    public string CustomPatternJson { get; set; } = "[]";
    public bool FillerEnabled { get; set; } = true;
    public bool FillerBeforeFirstProgram { get; set; } = true;
    public bool FillerBetweenPrograms { get; set; } = true;
    public bool FillerOnSourceChangeOnly { get; set; }
    public int MinFillerItems { get; set; } = 1;
    public int MaxFillerItems { get; set; } = 1;
    public int FillerRepeatWindow { get; set; } = 10;
}

public class OnDemandSourceRequest
{
    public Guid JellyfinItemId { get; set; }
    public int Weight { get; set; } = 1;
    public int BlockSize { get; set; } = 1;
    public ProgramPlaybackMode PlaybackMode { get; set; } = ProgramPlaybackMode.Sequential;
    public bool Enabled { get; set; } = true;
}

public class OnDemandSourceUpdateRequest
{
    public int Weight { get; set; } = 1;
    public int BlockSize { get; set; } = 1;
    public ProgramPlaybackMode PlaybackMode { get; set; } = ProgramPlaybackMode.Sequential;
    public bool Enabled { get; set; } = true;
    public int SortOrder { get; set; }
}

public class OnDemandFillerRequest
{
    public Guid JellyfinItemId { get; set; }
    public FillerContentKind Kind { get; set; } = FillerContentKind.Promo;
    public int Weight { get; set; } = 1;
    public bool Enabled { get; set; } = true;
}

public class OnDemandFillerUpdateRequest
{
    public FillerContentKind Kind { get; set; } = FillerContentKind.Promo;
    public int Weight { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public int SortOrder { get; set; }
}

public class OnDemandNextRequest
{
    public string ProgressKey { get; set; } = "test";
    public bool CompleteCurrent { get; set; }
}

public class OnDemandPositionRequest
{
    public string ProgressKey { get; set; } = "test";
    public Guid ItemId { get; set; }
    public long PositionTicks { get; set; }
}
