namespace Y2KMusicServer.Shared;

/// <summary>
/// Result of a black-box dump (<c>POST /api/admin/audio/blackbox/dump</c>):
/// the WAV files written under the data folder's <c>diagnostics</c> directory
/// — typically one per live deck tap plus the post-mix ring.
/// </summary>
public sealed class BlackBoxDumpDto
{
    public required List<string> Files { get; init; }
}
