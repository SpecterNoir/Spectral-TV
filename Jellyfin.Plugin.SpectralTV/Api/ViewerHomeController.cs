using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using Jellyfin.Plugin.SpectralTV.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SpectralTV.Api;

/// <summary>
/// Reads and writes the authenticated viewer's Spectral TV Home slot. Jellyfin only round-trips its
/// built-in Home-section values, so Spectral stores the selected slot in its own per-user configuration
/// while reserving that native slot with Live TV. The browser bridge replaces that row with channel cards.
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
    private const string SelectionPreferenceKey = "spectraltv-home-section-index";
    private const string NativeAnchorValue = "livetv";
    private static readonly Guid UserSettingsItemId = "usersettings".GetMD5();
    private static readonly object ConfigurationLock = new();

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

        var sectionIndex = GetConfiguredSection(userId);
        var changed = false;
        if (!sectionIndex.HasValue)
        {
            // Migrate both previous persistence attempts: the short-lived separate display key and
            // the unsupported custom value written directly into a native homesection field.
            if (preferences.TryGetValue(SelectionPreferenceKey, out var raw)
                && int.TryParse(raw, out var parsed)
                && parsed is >= 0 and < 10)
            {
                sectionIndex = parsed;
            }
            else
            {
                sectionIndex = FindSectionIndex(preferences);
            }

            if (sectionIndex.HasValue)
            {
                SetConfiguredSection(userId, sectionIndex);
            }
        }

        if (preferences.Remove(SelectionPreferenceKey))
        {
            changed = true;
        }

        if (sectionIndex.HasValue)
        {
            var nativeKey = $"{HomeSectionPreferencePrefix}{sectionIndex.Value}";
            if (!preferences.TryGetValue(nativeKey, out var nativeValue)
                || string.Equals(nativeValue, SectionValue, StringComparison.OrdinalIgnoreCase))
            {
                preferences[nativeKey] = NativeAnchorValue;
                changed = true;
            }
        }

        if (changed)
        {
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

        SetConfiguredSection(userId, request.SectionIndex);

        for (var index = 0; index < 10; index++)
        {
            var key = $"{HomeSectionPreferencePrefix}{index}";
            if (preferences.TryGetValue(key, out var current)
                && string.Equals(current, SectionValue, StringComparison.OrdinalIgnoreCase))
            {
                preferences[key] = request.SectionIndex == index ? NativeAnchorValue : "none";
            }
        }

        if (request.SectionIndex.HasValue)
        {
            preferences[$"{HomeSectionPreferencePrefix}{request.SectionIndex.Value}"] = NativeAnchorValue;
        }
        preferences.Remove(SelectionPreferenceKey);

        _displayPreferencesManager.SetCustomItemDisplayPreferences(
            userId,
            UserSettingsItemId,
            ClientName,
            preferences);

        return NoContent();
    }

    private static int? GetConfiguredSection(Guid userId)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return null;
        }

        lock (ConfigurationLock)
        {
            var match = (plugin.Configuration.ChannelsHomeSections ?? [])
                .FirstOrDefault(item => string.Equals(item.UserId, userId.ToString("N"), StringComparison.OrdinalIgnoreCase));
            return match is not null && match.SectionIndex is >= 0 and < 10
                ? match.SectionIndex
                : null;
        }
    }

    private static void SetConfiguredSection(Guid userId, int? sectionIndex)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        lock (ConfigurationLock)
        {
            var userIdValue = userId.ToString("N");
            var preferences = plugin.Configuration.ChannelsHomeSections ??= [];
            preferences.RemoveAll(item => string.Equals(item.UserId, userIdValue, StringComparison.OrdinalIgnoreCase));
            if (sectionIndex.HasValue)
            {
                preferences.Add(new ChannelsHomeSectionPreference
                {
                    UserId = userIdValue,
                    SectionIndex = sectionIndex.Value
                });
            }

            plugin.SaveConfiguration();
        }
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
