namespace Y2KMusicServer.Server.Scanning;

/// <summary>Where a scan currently is.</summary>
public enum ScanState
{
    Idle,
    Enumerating,
    Scanning,
    Completed,
    Failed
}

/// <summary>
/// Immutable snapshot of scan progress. Raised by <see cref="LibraryScanner"/>,
/// forwarded to clients over SignalR, and returned by the scan-status endpoint.
/// </summary>
public sealed record ScanProgress
{
    public ScanState State { get; init; }
    public int FilesFound { get; init; }
    public int FilesProcessed { get; init; }
    public int Added { get; init; }
    public int Skipped { get; init; }
    public string? CurrentPath { get; init; }
    public string? Message { get; init; }
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}
