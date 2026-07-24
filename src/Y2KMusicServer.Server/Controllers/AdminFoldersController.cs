using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Data;
using Y2KMusicServer.Server.Scanning;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// The global scan-folder list — the one place music folders are assigned
/// (replaces the retired per-category folder endpoints). Backed by
/// <see cref="ScanFolderStore"/> (JSON), not the database. Adding a folder
/// kicks a scan of just that folder; folder-scoped rescan / clear-data keep
/// the "innermost assigned folder wins" ownership rule from the category era.
/// </summary>
[ApiController]
[Route("api/admin/folders")]
public sealed class AdminFoldersController : ControllerBase
{
    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly IConfiguration _cfg;
    private readonly LibraryScanner _scanner;
    private readonly ILogger<AdminFoldersController> _log;

    public AdminFoldersController(IDbContextFactory<Y2KDbContext> dbf, IConfiguration cfg,
        LibraryScanner scanner, ILogger<AdminFoldersController> log)
    {
        _dbf = dbf;
        _cfg = cfg;
        _scanner = scanner;
        _log = log;
    }

    public sealed record FolderBody(string Path);

    /// <summary>Every assigned folder, with whether it currently exists on disk
    /// and how many tracks it owns (innermost-wins).</summary>
    [HttpGet]
    public async Task<object> List(CancellationToken ct)
    {
        var store = ScanFolderStore.Load(_cfg);
        var allPaths = store.Folders.Select(f => f.Path).ToList();

        await using var db = await _dbf.CreateDbContextAsync(ct);
        var items = new List<object>();
        foreach (var f in store.Folders)
        {
            var nested = FolderScope.NestedPrefixes(f.Path, allPaths);
            int owned = await db.Tracks.AsNoTracking().OwnedBy(f.Path, nested).CountAsync(ct);
            items.Add(new
            {
                f.Id,
                f.Path,
                Exists = SafeDirectoryExists(f.Path),
                TrackCount = owned
            });
        }
        return new { folders = items };
    }

    /// <summary>Adds a folder and kicks a scan of it. Idempotent on path;
    /// re-adding an existing folder just rescans it.</summary>
    [HttpPost]
    public IActionResult Add([FromBody] FolderBody? body)
    {
        var path = body?.Path?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return UnprocessableEntity(new { error = "path is required" });

        var entry = ScanFolderStore.Add(_cfg, path);
        _scanner.TryStart(entry.Id);
        _log.LogInformation("Scan folder added: {Path} (id {Id}); scan queued.", entry.Path, entry.Id);
        return Ok(new { entry.Id, entry.Path, Exists = SafeDirectoryExists(entry.Path) });
    }

    /// <summary>Removes a folder from the list. The tracks it owned stay in the
    /// library unless <c>clearData=true</c>, which removes them (and their
    /// playlist / request / mix-cache rows and on-disk caches) first.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Remove(int id, [FromQuery] bool clearData, CancellationToken ct)
    {
        int removedTracks = 0;
        if (clearData)
        {
            var r = await ClearOwnedTracksAsync(id, ct);
            if (r == null) return NotFound(new { error = "folder not found", id });
            removedTracks = r.Value;
        }

        var entry = ScanFolderStore.Remove(_cfg, id);
        if (entry == null) return NotFound(new { error = "folder not found", id });

        _log.LogInformation("Scan folder removed: {Path} (id {Id}), tracks removed: {N}.",
            entry.Path, entry.Id, removedTracks);
        return Ok(new { removed = entry.Path, removedTracks });
    }

    /// <summary>Rescans one folder (FIFO behind any running scan).</summary>
    [HttpPost("{id:int}/rescan")]
    public IActionResult Rescan(int id)
    {
        if (ScanFolderStore.Find(_cfg, id) == null)
            return NotFound(new { error = "folder not found", id });
        _scanner.TryStart(id);
        return Accepted(_scanner.Current);
    }

    /// <summary>
    /// Removes every track this folder owns (innermost-wins scoped) from the
    /// library, with the dependent rows and per-track caches. Nested assigned
    /// folders are untouched. Returns the number removed.
    /// </summary>
    [HttpPost("{id:int}/clear-data")]
    public async Task<IActionResult> ClearData(int id, CancellationToken ct)
    {
        var removed = await ClearOwnedTracksAsync(id, ct);
        return removed is int n ? Ok(new { removed = n }) : NotFound(new { error = "folder not found", id });
    }

    /// <summary>Null when the folder id is unknown; else the removed count.</summary>
    private async Task<int?> ClearOwnedTracksAsync(int id, CancellationToken ct)
    {
        var folder = ScanFolderStore.Find(_cfg, id);
        if (folder == null) return null;

        var nested = FolderScope.NestedPrefixes(folder.Path, ScanFolderStore.AllPaths(_cfg));

        await using var db = await _dbf.CreateDbContextAsync(ct);

        // Materialise the owned track ids first, for the on-disk cache cleanup.
        var ids = await db.Tracks.OwnedBy(folder.Path, nested).Select(t => t.Id).ToListAsync(ct);
        if (ids.Count == 0) return 0;

        // Reusable subquery (no giant IN list). MixCache FKs are Restrict, so its
        // pairs must go before the tracks; the playlist / saved-playlist / request
        // rows cascade, but we delete them explicitly so the result never depends
        // on the SQLite foreign-keys pragma being on.
        var ownedIds = db.Tracks.OwnedBy(folder.Path, nested).Select(t => t.Id);

        int removed;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.MixCache
                .Where(m => ownedIds.Contains(m.FromTrackId) || ownedIds.Contains(m.ToTrackId))
                .ExecuteDeleteAsync(ct);
            await db.PlaylistEntries.Where(p => ownedIds.Contains(p.TrackId)).ExecuteDeleteAsync(ct);
            await db.SavedPlaylistTracks.Where(p => ownedIds.Contains(p.TrackId)).ExecuteDeleteAsync(ct);
            await db.Requests.Where(r => ownedIds.Contains(r.TrackId)).ExecuteDeleteAsync(ct);
            removed = await db.Tracks.OwnedBy(folder.Path, nested).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        }

        // Best-effort: drop the per-track on-disk caches (orphaned after a re-scan
        // assigns new ids anyway). Never fail the request over a cache file.
        foreach (var tid in ids)
        {
            TryDelete(Path.Combine(DataPaths.PeaksDir(_cfg), tid + ".json"));
            TryDelete(Path.Combine(DataPaths.StructureDir(_cfg), tid + ".json"));
        }

        return removed;
    }

    private static void TryDelete(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }
}
