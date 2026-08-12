namespace Y2KMusicServer.Shared;

/// <summary>
/// Local-audio (server-machine speakers) status, served by
/// <c>GET /api/admin/audio/local</c> and consumed by the tray's Local audio
/// toggle + status line. "Local" means the sound card of the machine the
/// service runs on — the /stream broadcast is unaffected either way.
/// </summary>
public sealed class LocalAudioStatusDto
{
    /// <summary>Operator setting: decks try the sound card (true) or are
    /// forced to the silent pump (false).</summary>
    public required bool Enabled { get; init; }

    /// <summary>Friendly name of the default render device as the SERVICE
    /// process sees it, or null when it sees none (e.g. LocalSystem in a
    /// session with no audio endpoint).</summary>
    public string? DefaultDevice { get; init; }

    /// <summary>Active render devices visible to the service process.</summary>
    public int RenderDeviceCount { get; init; }

    /// <summary>Live decks and what each is actually outputting to.</summary>
    public required List<LocalAudioDeckDto> Decks { get; init; }
}

/// <summary>One live deck's output, for diagnostics.</summary>
public sealed class LocalAudioDeckDto
{
    /// <summary>Deck label ("A" / "B").</summary>
    public required string Deck { get; init; }

    /// <summary>"sound card" or "silent" — which output the deck actually got.</summary>
    public required string Output { get; init; }

    /// <summary>Playback state ("Playing" / "Paused" / "Stopped").</summary>
    public required string State { get; init; }

    /// <summary>Loaded track (Artist – Title), for context.</summary>
    public string? Track { get; init; }
}
