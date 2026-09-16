using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpectralTV.Api;

/// <summary>
/// Serves the small browser bridge used by Jellyfin Web. Keeping the script in its own request
/// makes successful loading testable and avoids relying on execution of a large inline script.
/// </summary>
[ApiController]
[Route("SpectralTV/web")]
public sealed class WebAssetsController : ControllerBase
{
    private const string BridgeResource = "Jellyfin.Plugin.SpectralTV.Configuration.nativeChannelsHome.js";
    private readonly ILogger<WebAssetsController> _logger;
    private static int _loggedFirstRequest;

    public WebAssetsController(ILogger<WebAssetsController> logger)
    {
        _logger = logger;
    }

    /// <summary>Returns the Spectral TV Channels Home browser bridge.</summary>
    [HttpGet("channels-home.js")]
    [AllowAnonymous]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult GetChannelsHomeBridge([FromQuery] string? v = null)
    {
        _ = v;
        var stream = typeof(WebAssetsController).Assembly.GetManifestResourceStream(BridgeResource);
        if (stream is null)
        {
            _logger.LogError("Spectral TV could not find embedded browser bridge resource {Resource}", BridgeResource);
            return NotFound();
        }

        if (Interlocked.Exchange(ref _loggedFirstRequest, 1) == 0)
        {
            _logger.LogInformation("Jellyfin Web requested the Spectral TV Channels Home bridge");
        }

        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Expires = "0";
        return File(stream, "application/javascript; charset=utf-8");
    }
}
