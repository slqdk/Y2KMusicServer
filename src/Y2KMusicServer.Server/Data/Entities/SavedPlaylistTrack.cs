namespace Y2KMusicServer.Server.Data.Entities;

/// <summary>
/// One ordered entry in a <see cref="SavedPlaylist"/>. Distinct from
/// <see cref="PlaylistEntry"/>, which remains the LIVE queue (now playing +
/// upcoming); a saved playlist is a stored track list the live queue can be
/// filled from (Activate / Auto DJ).
/// </summary>
public sealed class SavedPlaylistTrack
{
    public int Id { get; set; }

    public int SavedPlaylistId { get; set; }
    public SavedPlaylist? Playlist { get; set; }

    public int TrackId { get; set; }
    public Track? Track { get; set; }

    /// <summary>Zero-based position inside the playlist.</summary>
    public int Position { get; set; }

    public DateTime AddedAt { get; set; }
}
