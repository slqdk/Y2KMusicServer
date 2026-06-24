namespace Y2KMusicServer.Server.Diagnostics;

/// <summary>
/// One captured log line, flattened to what the admin page needs. Pushed live
/// over the hub as <c>logEntry</c> and returned by <c>GET /api/admin/logs</c>.
/// <see cref="Seq"/> is a per-process monotonic id so the client can order /
/// de-duplicate the live feed against an initial snapshot.
/// </summary>
public sealed record LogEntryDto
{
    public long Seq { get; init; }
    public DateTime TimestampUtc { get; init; }

    /// <summary>Verbose / Debug / Information / Warning / Error / Fatal.</summary>
    public string Level { get; init; } = "Information";

    /// <summary>Short SourceContext, e.g. "AudioEngine", "PlaybackLogger".</summary>
    public string Source { get; init; } = "";

    public string Message { get; init; } = "";
    public string? Exception { get; init; }
}
