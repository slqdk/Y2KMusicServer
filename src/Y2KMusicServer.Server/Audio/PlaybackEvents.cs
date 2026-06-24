namespace Y2KMusicServer.Server.Audio;

public enum PlaybackEngineState
{
    Stopped,
    Playing,
    Paused
}

/// <summary>What's on the current deck and its play state.</summary>
public sealed record NowPlayingInfo
{
    public int? TrackId { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public double DurationSec { get; init; }
    public PlaybackEngineState State { get; init; }
}

/// <summary>Periodic position update. <see cref="Deck"/> is "A" (current) or
/// "B" (incoming during a crossfade).</summary>
public sealed record DeckProgress
{
    public string Deck { get; init; } = "A";
    public int? TrackId { get; init; }
    public double PositionSec { get; init; }
    public double DurationSec { get; init; }
    /// <summary>Musical in-point of the track, in seconds. Meaningful for the
    /// cued/incoming Deck B (where the crossfade will enter); 0 otherwise.</summary>
    public double InPointSec { get; init; }
    /// <summary>Live BPM and beat-phase offset (seconds), so the admin can draw a
    /// scrolling beat-clock from position + BPM + phase. Null when unanalysed.</summary>
    public double? Bpm { get; init; }
    public double? PhaseOffsetSec { get; init; }
    public PlaybackEngineState State { get; init; }
}

/// <summary>Peak levels per channel for a deck's VU meter.</summary>
public sealed record VuSample
{
    public string Deck { get; init; } = "A";
    public float Left { get; init; }
    public float Right { get; init; }
}

/// <summary>A confirmed kick-drum onset on a deck, for the beat-line visual.</summary>
public sealed record BeatPulse
{
    public string Deck { get; init; } = "A";
    public float Strength { get; init; }
}

/// <summary>Emitted when a crossfade begins, so the admin page can show the
/// transition and why the engine chose these points.</summary>
public sealed record TransitionInfo
{
    public int? FromTrackId { get; init; }
    public int? ToTrackId { get; init; }
    public double TriggerSec { get; init; }
    public double FadeSeconds { get; init; }
    public bool SmartMix { get; init; }
    public bool BeatAligned { get; init; }
    public bool FadeShortened { get; init; }
    public string? SmartBeatState { get; init; }
    public string? Reason { get; init; }
}
