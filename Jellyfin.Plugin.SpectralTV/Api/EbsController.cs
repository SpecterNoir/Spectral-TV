using Jellyfin.Plugin.SpectralTV.Domain;
using Jellyfin.Plugin.SpectralTV.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SpectralTV.Api;

/// <summary>
/// Off-air display settings and custom slate uploads.
/// </summary>
[ApiController]
[Route("SpectralTV/api/ebs")]
[Authorize(Policy = Policies.RequiresElevation)]
public class EbsController : ControllerBase
{
    private readonly EbsService _ebs;

    public EbsController(EbsService ebs)
    {
        _ebs = ebs;
    }

    [HttpGet("settings")]
    public ActionResult<object> GetSettings()
    {
        var config = Plugin.Instance?.Configuration;
        var audioMode = config?.EbsAudioMode ?? EbsAudioMode.Silence;
        if (audioMode == EbsAudioMode.BackgroundMusic)
        {
            audioMode = EbsAudioMode.Silence;
        }

        return Ok(new
        {
            ebsDisplayMode = (int)(config?.EbsDisplayMode ?? EbsDisplayMode.SlateImage),
            ebsAudioMode = (int)audioMode,
            ebsSlateVariant = (int)(config?.EbsSlateVariant ?? EbsSlateVariant.Usa),

            // Legacy response keys stay present so an older cached admin.js cannot fail while
            // a browser is refreshing during an upgrade. Music itself is no longer exposed.
            ebsBackgroundMusicSource = 1,
            ebsBackgroundMusicLibraryName = string.Empty,
            ebsBackgroundMusicLibraryId = string.Empty,
            musicLibraries = Array.Empty<object>(),

            customSlates = _ebs.GetCustomSlateStatus(),
            stockSlates = new
            {
                usa = EbsService.EbsFolderName + "/offlineusa.jpg",
                international = EbsService.EbsFolderName + "/offline.jpg"
            }
        });
    }

    [HttpPut("settings")]
    public ActionResult<object> UpdateSettings([FromBody] EbsSettingsRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        if (request.EbsDisplayMode.HasValue)
        {
            plugin.Configuration.EbsDisplayMode = request.EbsDisplayMode.Value;
        }

        if (request.EbsAudioMode.HasValue)
        {
            plugin.Configuration.EbsAudioMode = request.EbsAudioMode.Value == EbsAudioMode.BackgroundMusic
                ? EbsAudioMode.Silence
                : request.EbsAudioMode.Value;
        }

        if (request.EbsSlateVariant.HasValue)
        {
            plugin.Configuration.EbsSlateVariant = request.EbsSlateVariant.Value;
        }

        // Clear any old music-library selections during the first save after upgrading.
        plugin.Configuration.EbsBackgroundMusicLibraryId = null;
        plugin.Configuration.EbsBackgroundMusicLibraryName = string.Empty;
        plugin.SaveConfiguration();
        return GetSettings();
    }

    [HttpPost("slates/{variant}")]
    [RequestSizeLimit(20_000_000)]
    public async Task<ActionResult<object>> UploadSlate(
        string variant,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file.Length == 0)
        {
            return BadRequest(new { message = "Image file is required." });
        }

        if (!TryParseVariant(variant, out var slateVariant))
        {
            return BadRequest(new { message = "Variant must be usa or international." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            await _ebs.UploadCustomSlateAsync(slateVariant, stream, file.FileName, cancellationToken);
            return Ok(new
            {
                variant = slateVariant.ToString(),
                customSlates = _ebs.GetCustomSlateStatus()
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("slates/{variant}")]
    public ActionResult DeleteSlate(string variant)
    {
        if (!TryParseVariant(variant, out var slateVariant))
        {
            return BadRequest(new { message = "Variant must be usa or international." });
        }

        _ebs.DeleteCustomSlate(slateVariant);
        return Ok(new { customSlates = _ebs.GetCustomSlateStatus() });
    }

    [HttpGet("slates/{variant}/image")]
    public ActionResult GetSlateImage(string variant)
    {
        if (!TryParseVariant(variant, out var slateVariant))
        {
            return BadRequest(new { message = "Variant must be usa or international." });
        }

        var path = _ebs.ResolveCustomSlatePath(slateVariant);
        if (string.IsNullOrWhiteSpace(path))
        {
            return NotFound();
        }

        var contentType = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? "image/png"
            : "image/jpeg";
        return PhysicalFile(path, contentType);
    }

    private static bool TryParseVariant(string value, out EbsSlateVariant variant)
    {
        if (string.Equals(value, "usa", StringComparison.OrdinalIgnoreCase))
        {
            variant = EbsSlateVariant.Usa;
            return true;
        }

        if (string.Equals(value, "international", StringComparison.OrdinalIgnoreCase))
        {
            variant = EbsSlateVariant.International;
            return true;
        }

        variant = default;
        return false;
    }
}

/// <summary>
/// Off-air settings payload. Legacy music fields are accepted and ignored so old browser tabs
/// can finish a request safely during an upgrade.
/// </summary>
public class EbsSettingsRequest
{
    public EbsDisplayMode? EbsDisplayMode { get; set; }

    public EbsAudioMode? EbsAudioMode { get; set; }

    public EbsSlateVariant? EbsSlateVariant { get; set; }

    public EbsBackgroundMusicSource? EbsBackgroundMusicSource { get; set; }

    public string? EbsBackgroundMusicLibraryName { get; set; }

    public string? EbsBackgroundMusicLibraryId { get; set; }
}
