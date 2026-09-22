using Jellyfin.Plugin.SpectralTV.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SpectralTV.Api;

/// <summary>Setup helpers for Jellyfin Live TV integration.</summary>
[ApiController]
[Route("SpectralTV/api/setup")]
public class SetupController : ControllerBase
{
    private readonly IServerApplicationHost _appHost;
    private readonly LiveTvIntegrationService _liveTvIntegration;
    private readonly PlayoutBuilderService _playoutBuilder;

    public SetupController(
        IServerApplicationHost appHost,
        LiveTvIntegrationService liveTvIntegration,
        PlayoutBuilderService playoutBuilder)
    {
        _appHost = appHost;
        _liveTvIntegration = liveTvIntegration;
        _playoutBuilder = playoutBuilder;
    }

    [HttpGet("urls")]
    [AllowAnonymous]
    public ActionResult<object> GetUrls()
    {
        try
        {
            return Ok(BuildUrlResponse());
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Could not build setup URLs: {ex.Message}" });
        }
    }

    [HttpGet("settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> GetSettings()
    {
        return Ok(new
        {
            publicBaseUrl = Plugin.Instance?.Configuration.PublicBaseUrl ?? string.Empty,
            playoutDaysToBuild = PlayoutScheduleHelper.GetPlayoutDaysToBuild()
        });
    }

    [HttpPut("settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> UpdateSettings([FromBody] SetupSettingsRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        plugin.Configuration.PublicBaseUrl = string.IsNullOrWhiteSpace(request.PublicBaseUrl)
            ? null
            : request.PublicBaseUrl.Trim().TrimEnd('/');
        plugin.SaveConfiguration();
        return Ok(BuildUrlResponse());
    }

    /// <summary>
    /// Reports whether Jellyfin's native Live TV configuration currently contains Spectral TV's
    /// local-loopback M3U tuner and XMLTV provider.
    /// </summary>
    [HttpGet("livetv-status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> GetLiveTvStatus()
    {
        try
        {
            return Ok(_liveTvIntegration.GetStatus());
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Could not inspect Jellyfin Live TV configuration: {ex.Message}" });
        }
    }

    /// <summary>
    /// Idempotently creates or repairs Spectral TV's native Live TV connection.
    /// The tuner always points at Jellyfin's local API address; public/reverse-proxy URLs are
    /// only exposed for external clients and are never required for the server to call itself.
    /// </summary>
    [HttpPost("livetv")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<object>> ConnectLiveTv(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is null)
        {
            return NotFound();
        }

        try
        {
            await _liveTvIntegration.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            _playoutBuilder.QueueLiveTvRefresh();
            return Ok(_liveTvIntegration.GetStatus());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499, new { message = "Live TV connection was cancelled." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                message = "Jellyfin Live TV could not be connected. No unrelated tuner or guide entries were changed.",
                detail = ex.Message
            });
        }
    }

    private object BuildUrlResponse()
    {
        var baseUrl = EpgService.GetPublicBaseUrl(Request, _appHost).TrimEnd('/');
        return new
        {
            baseUrl,
            m3u = $"{baseUrl}{LiveTvIntegrationService.SpectralM3uSuffix}",
            epg = $"{baseUrl}{LiveTvIntegrationService.SpectralXmlTvSuffix}"
        };
    }
}

public class SetupSettingsRequest
{
    public string? PublicBaseUrl { get; set; }
}

/// <summary>Manual schedule rebuild endpoint.</summary>
[ApiController]
[Route("SpectralTV/api/tasks")]
[Authorize(Policy = Policies.RequiresElevation)]
public class TasksController : ControllerBase
{
    private readonly PlayoutBuilderService _playoutBuilder;

    public TasksController(PlayoutBuilderService playoutBuilder)
    {
        _playoutBuilder = playoutBuilder;
    }

    [HttpPost("rebuild-all")]
    public IActionResult RebuildAll()
    {
        _playoutBuilder.QueueForceRebuildAllChannels();
        return Accepted(new { queued = true });
    }
}
