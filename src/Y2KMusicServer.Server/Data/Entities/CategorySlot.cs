namespace Y2KMusicServer.Server.Data.Entities;

/// <summary>
/// A time-of-day schedule slot for a category (legacy "Slot" 0..4). A category
/// can have up to five. <see cref="DaysMask"/> is a Mon..Sun bitfield
/// (bit 0 = Monday … bit 6 = Sunday). No slot rows exist on a fresh install;
/// they are created when the operator configures a schedule.
/// </summary>
public sealed class CategorySlot
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public Category? Category { get; set; }

    /// <summary>0..4.</summary>
    public int SlotIndex { get; set; }
    public bool Enabled { get; set; }

    /// <summary>Start time as "HH:mm".</summary>
    public string? TimeFromHHmm { get; set; }

    /// <summary>End time as "HH:mm".</summary>
    public string? TimeToHHmm { get; set; }

    /// <summary>Mon..Sun bitfield (bit 0 = Monday).</summary>
    public int DaysMask { get; set; }

    /// <summary>1..5.</summary>
    public int Priority { get; set; }
}
