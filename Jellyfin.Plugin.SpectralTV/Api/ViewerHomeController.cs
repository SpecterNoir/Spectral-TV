using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SpectralTV.Api;

/// <summary>
/// Stores the authenticated viewer's Spectral TV placement inside Jellyfin's normal Home settings.
/// Jellyfin only persists built-in HomeSectionType values, so Spectral keeps its custom slot index in
/// the same per-user display-preferences record without replacing any of Jellyfin's own preferences.
/// </summary>
[ApiController]
[Route("SpectralTV/api/viewer/home-section")]
[Authorize]
public sealed class ViewerHomeController : ControllerBase
{
    private const string JellyfinUserIdClaim = "Jellyfin-UserId";
    private const string ClientName = "emby";
    private const string PreferenceKey = "spectraltv-home-section-index";
    private static readonly Guid UserSettingsItemId = "usersettings".GetMD5();

    private readonly IDisplayPreferencesManager _displayPreferencesManager;

    public ViewerHomeController(IDisplayPreferencesManager displayPreferencesManager)
    {
        _displayPreferencesManager = displayPreferencesManager;
    }

    /// <summary>
    /// Returns the zero-based Jellyfin Home slot selected for Spectral TV Channels.
    /// </summary>
    [HttpGet]
    public ActionResult<object> Get()
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new { message = "This Jellyfin session is not associated with a user." });
        }

        var preferences = _displayPreferencesManager.ListCustomItemDisplayPreferences(
            userId,
            UserSettingsItemId,
            ClientName);

        int? sectionIndex = null;
        if (preferences.TryGetValue(PreferenceKey, out var raw)
            && int.TryParse(raw, out var parsed)
            && parsed is >= 0 and < 10)
        {
            sectionIndex = parsed;
        }

        return Ok(new { sectionIndex });
    }

    /// <summary>
    /// Saves or removes the viewer's Spectral TV Channels Home slot.
    /// </summary>
    [HttpPost]
    public IActionResult Set([FromBody] ViewerHomeSectionRequest request)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new { message = "This Jellyfin session is not associated with a user." });
        }

        if (request.SectionIndex is < 0 or >= 10)
        {
            return BadRequest(new { message = "Home section index must be between 0 and 9, or null to disable it." });
        }

        var preferences = _displayPreferencesManager.ListCustomItemDisplayPreferences(
            userId,
            UserSettingsItemId,
            ClientName);

        if (request.SectionIndex.HasValue)
        {
            preferences[PreferenceKey] = request.SectionIndex.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            preferences.Remove(PreferenceKey);
        }

        _displayPreferencesManager.SetCustomItemDisplayPreferences(
            userId,
            UserSettingsItemId,
            ClientName,
            preferences);

        return NoContent();
    }

    private bool TryGetUserId(out Guid userId)
    {
        var value = User.Claims
            .FirstOrDefault(claim => string.Equals(claim.Type, JellyfinUserIdClaim, StringComparison.OrdinalIgnoreCase))
            ?.Value;
        return Guid.TryParse(value, out userId) && userId != Guid.Empty;
    }
}

public sealed class ViewerHomeSectionRequest
{
    public int? SectionIndex { get; set; }
}
