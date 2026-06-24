namespace Y2KMusicServer.Server.Data.Entities;

/// <summary>
/// One ordered entry in the playlist. Replaces the legacy <c>playlist.dat</c>.
/// </summary>
public sealed class PlaylistEntry
{
    public int Id { get; set; }
    public int TrackId { get; set; }
    public Track? Track { get; set; }

    /// <summary>Zero-based position in the playlist.</summary>
    public int Position { get; set; }

    public string? AddedBy { get; set; }
    public DateTime AddedAt { get; set; }
    public PlaylistSource Source { get; set; }
}
