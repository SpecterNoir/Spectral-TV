namespace Jellyfin.Plugin.SpectralTV.Domain;

/// <summary>
/// Maps one Spectral TV on-demand channel to the private Jellyfin playlist materialized for a user.
/// This lets the smart sequence appear through Jellyfin's normal Playlists surface on ordinary clients.
/// </summary>
public class OnDemandPlaylistLink
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChannelId { get; set; }

    public Guid UserId { get; set; }

    public Guid JellyfinPlaylistId { get; set; }

    public int QueueProgramCount { get; set; } = 24;

    public DateTime LastSyncedAt { get; set; } = DateTime.UtcNow;
}
