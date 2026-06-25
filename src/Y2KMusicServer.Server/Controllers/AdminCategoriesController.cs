using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Data;
using Y2KMusicServer.Server.Data.Entities;
using Y2KMusicServer.Server.Scanning;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// Admin category + folder management. A minimal slice of the eventual Phase 4
/// admin API, enough to assign folders to categories so the scanner has
/// something to walk before the skinned UI exists. A minimal enable/disable
/// toggle exists here too (so Auto DJ is testable ahead of the UI);
/// custom-rename and the rest of the category UI land with Phase 4.
/// </summary>
[ApiController]
[Route("api/admin/categories")]
public sealed class AdminCategoriesController : ControllerBase
{
    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly LibraryScanner _scanner;
    private readonly IConfiguration _cfg;

    public AdminCategoriesController(IDbContextFactory<Y2KDbContext> dbf, LibraryScanner scanner, IConfiguration cfg)
    {
        _dbf = dbf;
        _scanner = scanner;
        _cfg = cfg;
    }

    public sealed record AddFolderRequest(string Path);
    public sealed record SlotInput(bool Enabled, string? TimeFromHHmm, string? TimeToHHmm, int DaysMask, int Priority);
    public sealed record RenameRequest(string Name);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);

        var cats = await db.Categories
            .OrderBy(c => c.DisplayOrder)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.IsCustom,
                c.Enabled,
                c.DisplayOrder,
                FolderCount = c.Folders.Count,
                TrackCount = db.Tracks.Count(t => t.CategoryId == c.Id)
            })
            .ToListAsync(ct);

        return Ok(cats);
    }

    [HttpGet("{id:int}/folders")]
    public async Task<IActionResult> Folders(int id, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        if (!await db.Categories.AnyAsync(c => c.Id == id, ct)) return NotFound();

        var folders = await db.CategoryFolders
            .Where(f => f.CategoryId == id)
            .Select(f => new { f.Id, f.Path })
            .ToListAsync(ct);

        return Ok(folders);
    }

    [HttpPost("{id:int}/folders")]
    public async Task<IActionResult> AddFolder(int id, [FromBody] AddFolderRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.Path))
            return BadRequest(new { error = "path is required" });

        await using var db = await _dbf.CreateDbContextAsync(ct);
        if (!await db.Categories.AnyAsync(c => c.Id == id, ct)) return NotFound();

        var path = req.Path.Trim();
        if (await db.CategoryFolders.AnyAsync(f => f.CategoryId == id && f.Path == path, ct))
            return Conflict(new { error = "folder already assigned to this category" });

        var folder = new CategoryFolder { CategoryId = id, Path = path };
        db.CategoryFolders.Add(folder);
        await db.SaveChangesAsync(ct);

        // First folder for a category that has no schedule yet: seed a default
        // 01:00-23:00, every-day, priority-3 slot so the category is immediately
        // Auto-DJ-eligible without opening the schedule editor. Times are "HH:mm"
        // (matches CategorySlot/the scheduler's TimeSpan parse); DaysMask 0 means
        // every day. Gated on "first folder AND no existing slots" so it seeds at
        // most once and never overwrites a schedule the operator set.
        int folderCount = await db.CategoryFolders.CountAsync(f => f.CategoryId == id, ct);
        bool hasSlots = await db.CategorySlots.AnyAsync(s => s.CategoryId == id, ct);
        if (folderCount == 1 && !hasSlots)
        {
            db.CategorySlots.Add(new CategorySlot
            {
                CategoryId = id,
                SlotIndex = 0,
                Enabled = true,
                TimeFromHHmm = "01:00",
                TimeToHHmm = "23:00",
                DaysMask = 0, // 0 = every day
                Priority = 3
            });
            await db.SaveChangesAsync(ct);
        }

        // Auto-scan the affected category so a newly-assigned folder is indexed
        // without a manual "Scan library" click. Fire-and-forget: the scan runs
        // on a background thread and reports over SignalR (scanProgress /
        // scanComplete). If a scan is already running this is a no-op and the
        // folder is picked up by the next scan.
        _scanner.TryStart(id);

        // onDiskNow is a hint, not a gate — the path is accepted even if the
        // drive isn't mounted right now.
        return Ok(new { folder.Id, folder.Path, onDiskNow = Directory.Exists(path) });
    }

    [HttpDelete("{id:int}/folders/{folderId:int}")]
    public async Task<IActionResult> RemoveFolder(int id, int folderId, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var folder = await db.CategoryFolders
            .FirstOrDefaultAsync(f => f.Id == folderId && f.CategoryId == id, ct);
        if (folder == null) return NotFound();

        db.CategoryFolders.Remove(folder);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Enables or disables a category. Enabling is refused for a category with
    /// no folders — the legacy rule (a folderless category can't feed playback
    /// or Auto DJ). Disabling is always allowed.
    /// </summary>
    [HttpPost("{id:int}/enable")]
    public async Task<IActionResult> SetEnabled(int id, [FromQuery] bool on, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var cat = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cat == null) return NotFound();

        if (on && !await db.CategoryFolders.AnyAsync(f => f.CategoryId == id, ct))
            return UnprocessableEntity(new
            {
                error = "category has no folders; assign a folder before enabling",
                id
            });

        cat.Enabled = on;
        await db.SaveChangesAsync(ct);
        return Ok(new { cat.Id, cat.Name, cat.Enabled });
    }

    /// <summary>The category's schedule slots (0..4), ordered.</summary>
    [HttpGet("{id:int}/slots")]
    public async Task<IActionResult> GetSlots(int id, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        if (!await db.Categories.AnyAsync(c => c.Id == id, ct)) return NotFound();

        var slots = await db.CategorySlots
            .Where(s => s.CategoryId == id)
            .OrderBy(s => s.SlotIndex)
            .Select(s => new
            {
                s.Id, s.SlotIndex, s.Enabled,
                s.TimeFromHHmm, s.TimeToHHmm, s.DaysMask, s.Priority
            })
            .ToListAsync(ct);

        return Ok(slots);
    }

    /// <summary>
    /// Replaces the category's schedule slots wholesale (max 5). SlotIndex is
    /// assigned from array order. DaysMask is the Mon..Sun bitfield
    /// (bit 0 = Monday); 0 means "every day". Priority is 1 (highest) .. 5.
    /// </summary>
    [HttpPut("{id:int}/slots")]
    public async Task<IActionResult> PutSlots(int id, [FromBody] List<SlotInput>? slots, CancellationToken ct)
    {
        if (slots == null) return BadRequest(new { error = "slots body required" });
        if (slots.Count > 5) return BadRequest(new { error = "at most 5 slots per category" });

        foreach (var s in slots)
        {
            if (s.Priority is < 1 or > 5) return BadRequest(new { error = "priority must be 1..5" });
            if (s.DaysMask is < 0 or > 127) return BadRequest(new { error = "daysMask must be 0..127" });
            if (s.Enabled &&
                (!TimeSpan.TryParse(s.TimeFromHHmm, out _) || !TimeSpan.TryParse(s.TimeToHHmm, out _)))
                return BadRequest(new { error = "an enabled slot needs valid HH:mm from/to times" });
        }

        await using var db = await _dbf.CreateDbContextAsync(ct);
        if (!await db.Categories.AnyAsync(c => c.Id == id, ct)) return NotFound();

        var existing = await db.CategorySlots.Where(s => s.CategoryId == id).ToListAsync(ct);
        db.CategorySlots.RemoveRange(existing);
        await db.SaveChangesAsync(ct); // clear first so the (CategoryId,SlotIndex) unique index can't clash

        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            db.CategorySlots.Add(new CategorySlot
            {
                CategoryId = id,
                SlotIndex = i,
                Enabled = s.Enabled,
                TimeFromHHmm = s.TimeFromHHmm,
                TimeToHHmm = s.TimeToHHmm,
                DaysMask = s.DaysMask,
                Priority = s.Priority
            });
        }
        await db.SaveChangesAsync(ct);
        return await GetSlots(id, ct);
    }

    /// <summary>Renames a custom category. Built-in categories cannot be renamed.</summary>
    [HttpPost("{id:int}/rename")]
    public async Task<IActionResult> Rename(int id, [FromBody] RenameRequest? req, CancellationToken ct)
    {
        var name = req?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { error = "name is required" });
        if (name.Length > 40) name = name[..40];

        await using var db = await _dbf.CreateDbContextAsync(ct);
        var cat = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cat == null) return NotFound();
        if (!cat.IsCustom)
            return UnprocessableEntity(new { error = "built-in categories cannot be renamed", id });

        cat.Name = name;
        await db.SaveChangesAsync(ct);
        return Ok(new { cat.Id, cat.Name, cat.IsCustom });
    }

    /// <summary>
    /// Clears all scanned data for a category — its Track rows and everything
    /// derived from them (mix-cache pairs, playlist entries, listener requests,
    /// and the on-disk peak/structure caches) — but KEEPS the category, its
    /// folders, and its schedule. Use it to wipe a category and re-scan clean.
    /// Returns the number of tracks removed. 404 if the category is unknown.
    /// </summary>
    [HttpPost("{id:int}/clear-data")]
    public async Task<IActionResult> ClearData(int id, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);

        var cat = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cat is null) return NotFound(new { error = "category not found", id });

        // Materialise the track ids first, for the on-disk cache cleanup below.
        var ids = await db.Tracks.Where(t => t.CategoryId == id).Select(t => t.Id).ToListAsync(ct);
        if (ids.Count == 0) return Ok(new { removed = 0 });

        // A reusable subquery (no giant IN list) for the bulk deletes. MixCache
        // FKs are Restrict, so its pairs must go before the tracks; PlaylistEntry
        // and Request cascade, but we delete them explicitly so the result never
        // depends on the SQLite foreign-keys pragma being on.
        var idQuery = db.Tracks.Where(t => t.CategoryId == id).Select(t => t.Id);

        int removed;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.MixCache
                .Where(m => idQuery.Contains(m.FromTrackId) || idQuery.Contains(m.ToTrackId))
                .ExecuteDeleteAsync(ct);
            await db.PlaylistEntries.Where(p => idQuery.Contains(p.TrackId)).ExecuteDeleteAsync(ct);
            await db.Requests.Where(r => idQuery.Contains(r.TrackId)).ExecuteDeleteAsync(ct);
            removed = await db.Tracks.Where(t => t.CategoryId == id).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        }

        // Best-effort: drop the per-track on-disk caches (orphaned after a re-scan
        // assigns new ids anyway). Never fail the request over a cache file.
        TryDeleteTrackCaches(ids);

        return Ok(new { removed });
    }

    private void TryDeleteTrackCaches(IReadOnlyCollection<int> ids)
    {
        foreach (var dir in new[] { DataPaths.PeaksDir(_cfg), DataPaths.StructureDir(_cfg) })
        {
            foreach (var trackId in ids)
            {
                try
                {
                    var file = Path.Combine(dir, $"{trackId}.json");
                    if (System.IO.File.Exists(file)) System.IO.File.Delete(file);
                }
                catch { /* best-effort: a stale cache file is harmless */ }
            }
        }
    }
}
