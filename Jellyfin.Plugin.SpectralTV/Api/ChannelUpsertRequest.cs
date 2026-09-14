using Jellyfin.Plugin.SpectralTV.Domain;

namespace Jellyfin.Plugin.SpectralTV.Api;

/// <summary>
/// Channel create/update payload from the admin UI.
/// </summary>
public class ChannelUpsertRequest
{
    public decimal Number { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public ChannelContentType ContentType { get; set; }

    public AspectRatioMode AspectRatio { get; set; }

    public bool ScanlinesEnabled { get; set; }

    public BugPlacementMode BugPlacement { get; set; }

    public Guid? LogoSetId { get; set; }

    public string? LogoFileName { get; set; }

    public string AudioLanguage { get; set; } = "eng";

    /// <summary>
    /// Retained only so older saved request/config shapes can still deserialize safely.
    /// Weather channels are no longer supported.
    /// </summary>
    public string? WeatherLocationQuery { get; set; }

    public Channel ToChannel()
    {
        if (ContentType is not ChannelContentType.TvShow and not ChannelContentType.Movie)
        {
            throw new ArgumentException("Spectral TV supports TV Show and Movie channels only.");
        }

        return new Channel
        {
            Number = Number,
            Name = Name,
            Enabled = Enabled,
            ContentType = ContentType,
            AspectRatio = AspectRatio,
            ScanlinesEnabled = ScanlinesEnabled,
            BugPlacement = BugPlacement,
            LogoSetId = LogoSetId,
            LogoFileName = LogoFileName,
            AudioLanguage = AudioLanguage,
            WeatherLocationQuery = null
        };
    }
}
