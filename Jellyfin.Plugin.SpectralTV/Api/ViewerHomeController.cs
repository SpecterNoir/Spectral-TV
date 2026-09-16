using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SpectralTV.Api;

/// <summary>
/// Reads and writes the authenticated viewer's Spectral TV placement in Jellyfin's own Home settings.
/// Jellyfin Web stores home slots as ordinary custom display preferences, so the Spectral section can
/// use the same homesection0...homesection9 values instead of maintaining duplicate state.
/// </summary>
[ApiController]
[Route("SpectralTV/api/viewer/home-section")]
[Authorize]
public sealed class ViewerHomeController : ControllerBase
{
    private const string JellyfinUserIdClaim = "Jellyfin-UserId";
    private const string ClientName = "emby";
    private const string SectionValue = "spectraltvchannels";
    private const string HomeSectionPreferencePrefix = "homesection";
    private const string LegacyPreferenceKey = "spectraltv-home-section-index";
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

        var sectionIndex = FindSectionIndex(preferences);

        // Migrate the short-lived 0.0.3.125-0.0.3.135 duplicate preference on first read.
        if (!sectionIndex.HasValue
            && preferences.TryGetValue(LegacyPreferenceKey, out var raw)
            && int.TryParse(raw, out var parsed)
            && parsed is >= 0 and < 10)
        {
            sectionIndex = parsed;
            preferences[$"{HomeSectionPreferencePrefix}{parsed}"] = SectionValue;
            preferences.Remove(LegacyPreferenceKey);
            _displayPreferencesManager.SetCustomItemDisplayPreferences(
                userId,
                UserSettingsItemId,
                ClientName,
                preferences);
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

        for (var index = 0; index < 10; index++)
        {
            var key = $"{HomeSectionPreferencePrefix}{index}";
            if (preferences.TryGetValue(key, out var current)
                && string.Equals(current, SectionValue, StringComparison.OrdinalIgnoreCase))
            {
                preferences[key] = "none";
            }
        }

        if (request.SectionIndex.HasValue)
        {
            preferences[$"{HomeSectionPreferencePrefix}{request.SectionIndex.Value}"] = SectionValue;
        }

        preferences.Remove(LegacyPreferenceKey);

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

    private static int? FindSectionIndex(IReadOnlyDictionary<string, string?> preferences)
    {
        for (var index = 0; index < 10; index++)
        {
            if (preferences.TryGetValue($"{HomeSectionPreferencePrefix}{index}", out var value)
                && string.Equals(value, SectionValue, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return null;
    }
}

public sealed class ViewerHomeSectionRequest
{
    public int? SectionIndex { get; set; }
}
