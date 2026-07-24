namespace Y2KMusicServer.Server.Data.Entities;

/// <summary>
/// A scanned audio file. Mirrors the legacy WinForms <c>TrackInfo</c>
/// descriptive fields; pair-specific mix points are normalised out into
/// <see cref="MixCache"/> rather than living on the track (legacy stored a
/// single pairing on TrackInfo via OutPointPairedWith).
/// </summary>
public sealed class Track
{
    public int Id { get; set; }

    /// <summary>Absolute path on disk. Unique across the library.</summary>
    public string FilePath { get; set; } = null!;

    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }

    /// <summary>Release year. Null when unknown (legacy stored uint, 0 = unknown).</summary>
    public int? Year { get; set; }

    public string? Genre { get; set; }

    /// <summary>File kind, e.g. "MP3" / "WAV" / "FLAC".</summary>
    public string? Type { get; set; }

    /// <summary>Duration in seconds (legacy stored a TimeSpan).</summary>
    public double DurationSec { get; set; }

    /// <summary>Detected or tagged tempo. Null when not yet analysed.</summary>
    public double? Bpm { get; set; }

    /// <summary>
    /// EBU R128 integrated loudness. Null until a Phase 5 LUFS rescan populates
    /// it. This is NOT the same measurement as the legacy RMS dBFS loudness, so
    /// legacy loudness values are intentionally not carried into this column.
    /// </summary>
    public double? LufsIntegrated { get; set; }

    /// <summary>
    /// Operator-set genre bucket that overrides the genre-map resolution of the
    /// raw tag <see cref="Genre"/>. Null = follow the map. The effective bucket
    /// is computed at query time (GenreMapStore.EffectiveGenre), never stored.
    /// </summary>
    public string? GenreOverride { get; set; }

    /// <summary>Album-art path or URL. Null = read from file tags on demand.</summary>
    public string? AlbumArt { get; set; }

    public DateTime? ScannedAt { get; set; }

    /// <summary>BPM detector confidence (Phase 5). Null until analysed.</summary>
    public double? BpmConfidence { get; set; }

    /// <summary>Downbeat phase offset in seconds (Phase 5). Null until analysed.</summary>
    public double? BeatPhaseOffsetSec { get; set; }
}
