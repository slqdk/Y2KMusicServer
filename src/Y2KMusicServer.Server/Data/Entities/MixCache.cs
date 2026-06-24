namespace Y2KMusicServer.Server.Data.Entities;

/// <summary>
/// Pair-specific mix analysis. A row says: when transitioning from
/// <see cref="FromTrackId"/> into <see cref="ToTrackId"/>, fade A out starting
/// at <see cref="OutPoint"/> and bring B in at <see cref="InPoint"/>. Mix
/// points are only meaningful for the exact pairing — the contract carries
/// over from the legacy version, where this lived on TrackInfo via
/// OutPointPairedWith. Unique on (FromTrackId, ToTrackId).
/// </summary>
public sealed class MixCache
{
    public int Id { get; set; }
    public int FromTrackId { get; set; }
    public int ToTrackId { get; set; }

    /// <summary>Seconds into A at which the fade-out begins.</summary>
    public double OutPoint { get; set; }

    /// <summary>Seconds into B at which B is seeked to / faded in.</summary>
    public double InPoint { get; set; }

    /// <summary>Ideal fade overlap in seconds for this pair.</summary>
    public double FadeDurationSec { get; set; }

    /// <summary>Pair-quality score from the last analysis.</summary>
    public double PairScore { get; set; }

    /// <summary>Human-readable reason from the last analysis.</summary>
    public string? Reason { get; set; }

    public bool BeatAligned { get; set; }
    public DateTime ComputedAt { get; set; }
}
