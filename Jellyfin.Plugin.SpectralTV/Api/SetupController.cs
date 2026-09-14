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

    public SetupController(IServerApplicationHost appHost)
    {
        _appHost = appHost;
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

    private object BuildUrlResponse()
    {
        var baseUrl = EpgService.GetPublicBaseUrl(Request, _appHost);
        return new
        {
            baseUrl,
            m3u = $"{baseUrl}/SpectralTV/iptv/channels.m3u",
            epg = $"{baseUrl}/SpectralTV/iptv/epg.xml"
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
