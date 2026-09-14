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

    public AspectRatioMode AspectRatio { get; set; }

    public bool ScanlinesEnabled { get; set; }

    public BugPlacementMode BugPlacement { get; set; }

    public Guid? LogoSetId { get; set; }

    public string? LogoFileName { get; set; }

    public Channel ToChannel()
    {
        var channel = new Channel
        {
            Number = Number,
            Name = Name,
            Enabled = Enabled,
            ContentType = ChannelContentType.TvShow,
            AspectRatio = AspectRatio,
            ScanlinesEnabled = ScanlinesEnabled,
            BugPlacement = BugPlacement,
            LogoSetId = LogoSetId,
            LogoFileName = LogoFileName
        };

        return channel;
    }
}
