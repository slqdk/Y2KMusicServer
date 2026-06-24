namespace Y2KMusicServer.Shared;

/// <summary>
/// Update availability info. Populated by the service's
/// GitHubUpdateChecker; consumed by the tray.
/// </summary>
public sealed class UpdateInfoDto
{
    public required bool Available { get; init; }
    public required string CurrentVersion { get; init; }
    public string? LatestVersion { get; init; }
    public string? DownloadUrl { get; init; }
    public string? ReleaseNotesUrl { get; init; }
    public DateTime? PublishedAtUtc { get; init; }
    public string? CheckError { get; init; }
}
