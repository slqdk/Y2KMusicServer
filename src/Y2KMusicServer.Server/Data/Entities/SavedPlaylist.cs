namespace Y2KMusicServer.Server.Data.Entities;

/// <summary>
/// A saved (named) playlist — one of up to 14 operator-built lists shown as
/// tiles above the live queue. Replaces the category model as Auto DJ's track
/// source: a playlist carries its own schedule slots and a single 1–5
/// priority; when several playlists have an active slot, Auto DJ picks the
/// source playlist by priority-weighted random.
/// </summary>
public sealed class SavedPlaylist
{
    public const int MaxPlaylists = 14;
    public const int MaxSlots = 5;

    public int Id { get; set; }
    public string Name { get; set; } = null!;

    /// <summary>1–5. Weight for Auto DJ's source pick when several playlists
    /// have an active timeslot (5 feeds five times as often as 1).</summary>
    public int Priority { get; set; } = 3;

    /// <summary>Tile position in the admin grid (0-based).</summary>
    public int TileOrder { get; set; }

    public DateTime CreatedAt { get; set; }

    public ICollection<SavedPlaylistTrack> Tracks { get; set; } = new List<SavedPlaylistTrack>();
    public ICollection<SavedPlaylistSlot> Slots { get; set; } = new List<SavedPlaylistSlot>();
}
