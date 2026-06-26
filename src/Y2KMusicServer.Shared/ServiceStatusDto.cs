namespace Y2KMusicServer.Shared;

/// <summary>
/// Snapshot of the service for the tray and admin page.
/// Returned by GET /api/admin/service/status.
/// </summary>
public sealed class ServiceStatusDto
{
    public required string Version { get; init; }
    public required DateTime StartedAtUtc { get; init; }
    public required string MachineName { get; init; }
    public required int KestrelPort { get; init; }
    public required string AdminUrl { get; init; }
    public required string ListenerUrl { get; init; }

    /// <summary>Absolute path of the data root (<c>C:\ProgramData\Y2KMusicServer</c>
    /// in production) — what the tray's "Open data folder" opens. Null on older
    /// services that don't report it.</summary>
    public string? DataPath { get; init; }

    public UpdateInfoDto? Update { get; init; }
}
