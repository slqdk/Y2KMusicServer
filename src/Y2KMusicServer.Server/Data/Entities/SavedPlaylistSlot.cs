namespace Y2KMusicServer.Server.Data.Entities;

/// <summary>
/// A time-of-day schedule slot for a saved playlist (up to five per playlist,
/// same shape as the retired per-category slots). <see cref="DaysMask"/> is a
/// Mon..Sun bitfield (bit 0 = Monday … bit 6 = Sunday; 0 = every day). A
/// window may wrap past midnight. Priority lives on the playlist itself, not
/// per slot.
/// </summary>
public sealed class SavedPlaylistSlot
{
    public int Id { get; set; }

    public int SavedPlaylistId { get; set; }
    public SavedPlaylist? Playlist { get; set; }

    /// <summary>0..4.</summary>
    public int SlotIndex { get; set; }
    public bool Enabled { get; set; }

    /// <summary>Start time as "HH:mm".</summary>
    public string? TimeFromHHmm { get; set; }

    /// <summary>End time as "HH:mm".</summary>
    public string? TimeToHHmm { get; set; }

    /// <summary>Mon..Sun bitfield (bit 0 = Monday). 0 = every day.</summary>
    public int DaysMask { get; set; }
}
