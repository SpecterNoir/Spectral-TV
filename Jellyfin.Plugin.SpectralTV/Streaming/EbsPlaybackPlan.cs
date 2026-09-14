using Jellyfin.Plugin.SpectralTV.Domain;

namespace Jellyfin.Plugin.SpectralTV.Streaming;

public sealed class EbsPlaybackPlan
{
    public EbsDisplayMode DisplayMode { get; init; } = EbsDisplayMode.SlateImage;

    public EbsAudioMode AudioMode { get; init; } = EbsAudioMode.Silence;

    public string? SlateImagePath { get; init; }

    /// <summary>
    /// Retained for compatibility with older call sites. Spectral TV no longer selects music for off-air playback.
    /// </summary>
    public string? MusicPath { get; init; }

    public double DurationSeconds { get; init; }
}
