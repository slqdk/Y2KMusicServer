namespace Y2KMusicServer.Server.Audio;

/// <summary>Where the audio-analysis pass currently is.</summary>
public enum AnalysisState
{
    Idle,
    Running,
    Completed,
    Failed
}

/// <summary>
/// Immutable snapshot of the analysis pass. Raised by
/// <see cref="AudioAnalysisService"/>, forwarded over SignalR
/// (<c>analyzeProgress</c> / <c>analyzeComplete</c>), and returned by the
/// analyze-status endpoint. Mirrors <c>ScanProgress</c>.
/// </summary>
public sealed record AnalysisProgress
{
    public AnalysisState State { get; init; }
    public int Total { get; init; }
    public int Processed { get; init; }
    public int Updated { get; init; }
    public int Failed { get; init; }
    public string? CurrentTitle { get; init; }
    public string? Message { get; init; }
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}
