using Microsoft.AspNetCore.Mvc;
using Y2KMusicServer.Server.Network;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// Read-only server-side directory browsing for the admin folder picker.
///
/// The service is headless and runs on the server host, so the operator's
/// browser cannot see the server's filesystem. This endpoint lets the picker
/// enumerate drives and the immediate subdirectories of a path on the host, so
/// folders can be chosen visually instead of typed.
///
/// SECURITY POSTURE: the whole admin API is unauthenticated by design — this is
/// a single-operator LAN tool (see architecture.md). This endpoint therefore
/// lets anyone who can reach the port enumerate directory NAMES anywhere on the
/// host disk. It is deliberately read-only and never returns file contents,
/// only directory structure. If the service is ever exposed beyond a trusted
/// LAN, this endpoint and the rest of /api/admin must be placed behind auth.
/// </summary>
[ApiController]
[Route("api/admin/fs")]
public sealed class AdminFileSystemController : ControllerBase
{
    /// <summary>A directory (or drive) entry in a listing.</summary>
    public sealed record FsEntry(string Name, string Path);

    /// <summary>
    /// One directory level. <c>Path</c> is null at the drive list. <c>Parent</c>
    /// is where "up" goes: the containing directory, "" for the drive list (when
    /// at a drive root), or null when already at the drive list.
    /// </summary>
    public sealed record FsListing(string? Path, string? Parent, bool IsDriveList, IReadOnlyList<FsEntry> Entries);

    private readonly NetworkShareConnector _connector;

    public AdminFileSystemController(NetworkShareConnector connector) => _connector = connector;

    [HttpGet]
    public IActionResult Browse([FromQuery] string? path)
    {
        // No path => the drive list, which is the root of the browser.
        if (string.IsNullOrWhiteSpace(path))
            return Ok(DriveList());

        path = path.Trim();

        // Network path: authenticate the service's session to the server first
        // (using any stored credential for its host) so a credentialed share is
        // readable here. No-op for local paths or hosts with no stored credential.
        if (OperatingSystem.IsWindows())
            _connector.EnsureConnected(path);

        if (!Directory.Exists(path))
            return NotFound(new { error = "That folder no longer exists on the server." });

        // Where "up" goes. The parent of a drive root is null, which we map to ""
        // so the UI returns to the drive list.
        string? parent;
        try { parent = new DirectoryInfo(path).Parent?.FullName ?? ""; }
        catch { parent = ""; }

        var entries = new List<FsEntry>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(name)) name = dir;
                entries.Add(new FsEntry(name, dir));
            }
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403, new { error = "Access to that folder is denied." });
        }
        catch (Exception)
        {
            return StatusCode(500, new { error = "Could not read that folder." });
        }

        entries.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return Ok(new FsListing(path, parent, IsDriveList: false, entries));
    }

    private static FsListing DriveList()
    {
        var entries = new List<FsEntry>();
        foreach (var d in DriveInfo.GetDrives())
        {
            var label = d.Name; // e.g. "C:\"
            try
            {
                if (d.IsReady && !string.IsNullOrWhiteSpace(d.VolumeLabel))
                    label = $"{d.Name} ({d.VolumeLabel})";
            }
            catch { /* not ready / inaccessible — keep the bare drive name */ }
            entries.Add(new FsEntry(label, d.Name));
        }
        return new FsListing(Path: null, Parent: null, IsDriveList: true, entries);
    }
}
