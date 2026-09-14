using Jellyfin.Plugin.SpectralTV.Domain;
using Jellyfin.Plugin.SpectralTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SpectralTV.Api;

/// <summary>Upload and serve user-owned channel logos.</summary>
[ApiController]
[Route("SpectralTV/api/logos")]
[Authorize(Policy = Policies.RequiresElevation)]
public class LogosController : ControllerBase
{
    private readonly LogoSetService _logos;

    public LogosController(LogoSetService logos)
    {
        _logos = logos;
    }

    [HttpGet("sets")]
    public async Task<ActionResult<object>> GetSets(CancellationToken cancellationToken)
    {
        var sets = await _logos.GetAllAsync(cancellationToken);
        return Ok(sets.Where(LogoSetService.IsCustomSet).Select(MapSet));
    }

    [HttpPost("sets/custom")]
    public async Task<ActionResult<object>> CreateCustomSet([FromBody] CreateCustomLogoSetRequest request, CancellationToken cancellationToken)
    {
        var set = await _logos.CreateCustomSetAsync(request.Name, cancellationToken);
        return Ok(MapSet(set));
    }

    [HttpPost("sets/{setId:guid}/logos")]
    [RequestSizeLimit(20_000_000)]
    public async Task<ActionResult<object>> Upload(
        Guid setId,
        IFormFile file,
        [FromForm] string? displayName,
        CancellationToken cancellationToken)
    {
        if (file.Length == 0) return BadRequest(new { message = "Choose a logo image first." });
        try
        {
            await using var stream = file.OpenReadStream();
            var entry = await _logos.UploadCustomLogoAsync(
                setId,
                stream,
                file.FileName,
                displayName ?? Path.GetFileNameWithoutExtension(file.FileName),
                cancellationToken);
            return Ok(MapEntry(entry));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("sets/{setId:guid}/entries/{entryId:guid}")]
    public async Task<IActionResult> DeleteEntry(Guid setId, Guid entryId, CancellationToken cancellationToken)
    {
        try
        {
            await _logos.DeleteCustomLogoAsync(setId, entryId, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("{channelId:guid}/{fileName}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetChannelLogo(
        Guid channelId,
        string fileName,
        [FromServices] ChannelService channels,
        CancellationToken cancellationToken)
    {
        var channel = await channels.GetByIdAsync(channelId, cancellationToken);
        if (channel?.LogoSetId is null || !fileName.Equals(channel.LogoFileName, StringComparison.OrdinalIgnoreCase)) return NotFound();
        var set = await _logos.GetByIdAsync(channel.LogoSetId.Value, cancellationToken);
        var entry = set?.Entries.FirstOrDefault(item => item.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (set is null || entry is null) return NotFound();
        var path = _logos.ResolveLogoPath(set, entry.RelativePath);
        return path is null ? NotFound() : PhysicalFile(path, ContentTypeFor(path));
    }

    private static object MapSet(LogoSet set) => new
    {
        id = set.Id,
        name = set.Name,
        isCustom = true,
        entries = set.Entries.OrderBy(item => item.DisplayName).Select(MapEntry).ToList()
    };

    private static object MapEntry(LogoSetEntry entry) => new
    {
        id = entry.Id,
        logoSetId = entry.LogoSetId,
        fileName = entry.FileName,
        displayName = entry.DisplayName
    };

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "image/png"
    };
}

public class CreateCustomLogoSetRequest
{
    public string Name { get; set; } = string.Empty;
}
