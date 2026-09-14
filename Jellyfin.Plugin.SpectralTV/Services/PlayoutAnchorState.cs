namespace Jellyfin.Plugin.SpectralTV.Services;

/// <summary>Persists sequential episode positions and recent break clips across schedule builds.</summary>
public class PlayoutAnchorState
{
    public Dictionary<Guid, int> ProgramSourceCursor { get; set; } = new();

    public List<Guid> RecentFillerSourceIds { get; set; } = new();
}
