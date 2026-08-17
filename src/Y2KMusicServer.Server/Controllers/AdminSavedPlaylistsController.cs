using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Data;
using Y2KMusicServer.Server.Data.Entities;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// Saved playlists — the up-to-14 named track lists shown as tiles above the
/// live queue, which Auto DJ sources from (via their schedule slots and 1–5
/// priority). CRUD + membership + slots. Activation (replace the live queue by
/// crossfade) is a later ship and lives with the playback surface, not here.
/// </summary>
[ApiController]
[Route("api/admin/saved-playlists")]
public sealed class AdminSavedPlaylistsController : ControllerBase
{
    private readonly IDbContextFactory<Y2KDbContext> _dbf;

    public AdminSavedPlaylistsController(IDbContextFactory<Y2KDbContext> dbf) => _dbf = dbf;

    public sealed record NameBody(string Name);
    public sealed record AddTrackBody(int TrackId);
    public sealed record SlotBody(int SlotIndex, bool Enabled, string? TimeFrom, string? TimeTo, int DaysMask);

    // ── Playlist CRUD ─────────────────────────────────────────────────────────

    /// <summary>All playlists in tile order, with track counts.</summary>
    [HttpGet]
    public async Task<object> List(CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var items = await db.SavedPlaylists.AsNoTracking()
            .OrderBy(p => p.TileOrder)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Priority,
                p.TileOrder,
                TrackCount = p.Tracks.Count,
                SlotCount = p.Slots.Count(s => s.Enabled)
            })
            .ToListAsync(ct);
        return new { playlists = items, max = SavedPlaylist.MaxPlaylists };
    }

    /// <summary>Creates a playlist (cap 14). 422 when full or the name is
    /// blank/duplicate.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] NameBody? body, CancellationToken ct)
    {
        var name = body?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return UnprocessableEntity(new { error = "name is required" });

        await using var db = await _dbf.CreateDbContextAsync(ct);

        if (await db.SavedPlaylists.CountAsync(ct) >= SavedPlaylist.MaxPlaylists)
            return UnprocessableEntity(new { error = $"maximum {SavedPlaylist.MaxPlaylists} playlists" });
        if (await db.SavedPlaylists.AnyAsync(p => p.Name.ToLower() == name.ToLower(), ct))
            return UnprocessableEntity(new { error = "a playlist with that name already exists" });

        int order = await db.SavedPlaylists.Select(p => (int?)p.TileOrder).MaxAsync(ct) is int m ? m + 1 : 0;
        var pl = new SavedPlaylist { Name = name, Priority = 3, TileOrder = order, CreatedAt = DateTime.UtcNow };
        // Seed an always-on default slot (00:00–23:59, every day) so a new
        // playlist is immediately eligible for Auto DJ without opening the
        // schedule editor — mirroring the old first-folder slot seeding. It
        // seeds once, at creation; the editor replaces it freely.
        pl.Slots.Add(new SavedPlaylistSlot
        {
            SlotIndex = 0,
            Enabled = true,
            TimeFromHHmm = "00:00",
            TimeToHHmm = "23:59",
            DaysMask = 0
        });
        db.SavedPlaylists.Add(pl);
        await db.SaveChangesAsync(ct);
        return Ok(new { pl.Id, pl.Name, pl.Priority, pl.TileOrder });
    }

    [HttpPost("{id:int}/rename")]
    public async Task<IActionResult> Rename(int id, [FromBody] NameBody? body, CancellationToken ct)
    {
        var name = body?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return UnprocessableEntity(new { error = "name is required" });

        await using var db = await _dbf.CreateDbContextAsync(ct);
        var pl = await db.SavedPlaylists.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pl == null) return NotFound();
        if (await db.SavedPlaylists.AnyAsync(p => p.Id != id && p.Name.ToLower() == name.ToLower(), ct))
            return UnprocessableEntity(new { error = "a playlist with that name already exists" });

        pl.Name = name;
        await db.SaveChangesAsync(ct);
        return Ok(new { pl.Id, pl.Name });
    }

    /// <summary>1–5; weight for Auto DJ's source pick when several playlists
    /// have an active timeslot.</summary>
    [HttpPost("{id:int}/priority")]
    public async Task<IActionResult> Priority(int id, [FromQuery] int value, CancellationToken ct)
    {
        if (value is < 1 or > 5)
            return UnprocessableEntity(new { error = "priority must be 1..5" });

        await using var db = await _dbf.CreateDbContextAsync(ct);
        var pl = await db.SavedPlaylists.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pl == null) return NotFound();
        pl.Priority = value;
        await db.SaveChangesAsync(ct);
        return Ok(new { pl.Id, pl.Priority });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var pl = await db.SavedPlaylists.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pl == null) return NotFound();
        db.SavedPlaylists.Remove(pl); // tracks + slots cascade
        await db.SaveChangesAsync(ct);
        return Ok(new { deleted = pl.Name });
    }

    // ── Export / import ───────────────────────────────────────────────────────

    /// <summary>
    /// Exports a playlist as a portable JSON file. Tracks travel as tags +
    /// file path, not database ids, so the file survives a rescan or another
    /// machine: import matches by path first, then artist+title.
    /// </summary>
    [HttpGet("{id:int}/export")]
    public async Task<IActionResult> Export(int id, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var pl = await db.SavedPlaylists.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pl == null) return NotFound();

        var tracks = await db.SavedPlaylistTracks.AsNoTracking()
            .Where(t => t.SavedPlaylistId == id)
            .OrderBy(t => t.Position)
            .Select(t => new
            {
                title = t.Track!.Title,
                artist = t.Track!.Artist,
                album = t.Track!.Album,
                durationSec = t.Track!.DurationSec,
                filePath = t.Track!.FilePath
            })
            .ToListAsync(ct);

        var payload = new
        {
            format = "y2k-playlist",
            version = 1,
            name = pl.Name,
            exportedAt = DateTime.UtcNow,
            tracks
        };
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        var safe = string.Join("_", pl.Name.Split(Path.GetInvalidFileNameChars(),
            StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (safe.Length == 0) safe = "playlist";
        return File(bytes, "application/json", $"{safe}.y2kpl.json");
    }

    public sealed class ImportTrack
    {
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? FilePath { get; set; }
    }

    public sealed class ImportBody
    {
        public string? Name { get; set; }
        public List<ImportTrack>? Tracks { get; set; }
    }

    /// <summary>
    /// Imports a playlist file produced by export (extra JSON fields are
    /// ignored). Creates a new playlist — an existing name gets " (2)" etc. —
    /// and matches each track against the library: exact file path first,
    /// then artist+title (FLAC preferred on ties, mirroring listener dedupe).
    /// Unmatched tracks are skipped and reported, never invented.
    /// </summary>
    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] ImportBody? body, CancellationToken ct)
    {
        var baseName = body?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(baseName) || body?.Tracks == null || body.Tracks.Count == 0)
            return UnprocessableEntity(new { error = "not a playlist export (name and tracks required)" });

        await using var db = await _dbf.CreateDbContextAsync(ct);
        if (await db.SavedPlaylists.CountAsync(ct) >= SavedPlaylist.MaxPlaylists)
            return UnprocessableEntity(new { error = $"maximum {SavedPlaylist.MaxPlaylists} playlists" });

        // Unique name: "Party", "Party (2)", "Party (3)" …
        var existingNames = (await db.SavedPlaylists.Select(p => p.Name).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = baseName;
        for (int n = 2; existingNames.Contains(name); n++) name = $"{baseName} ({n})";

        // Library lookups: path → track, and artist|title → best track.
        var all = await db.Tracks.AsNoTracking()
            .Select(t => new { t.Id, t.Title, t.Artist, t.FilePath, t.Type })
            .ToListAsync(ct);
        var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in all) byPath.TryAdd(t.FilePath, t.Id);
        var byTag = all
            .Where(t => !string.IsNullOrWhiteSpace(t.Title))
            .GroupBy(t => $"{(t.Artist ?? "").Trim().ToLowerInvariant()}|{t.Title!.Trim().ToLowerInvariant()}")
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(t => string.Equals(t.Type, "FLAC", StringComparison.OrdinalIgnoreCase))
                      .ThenBy(t => t.Id).First().Id);

        int order = await db.SavedPlaylists.Select(p => (int?)p.TileOrder).MaxAsync(ct) is int m ? m + 1 : 0;
        var pl = new SavedPlaylist { Name = name, Priority = 3, TileOrder = order, CreatedAt = DateTime.UtcNow };
        // Same always-on seed slot a fresh playlist gets from Create.
        pl.Slots.Add(new SavedPlaylistSlot
        {
            SlotIndex = 0,
            Enabled = true,
            TimeFromHHmm = "00:00",
            TimeToHHmm = "23:59",
            DaysMask = 0
        });

        int pos = 0;
        var missing = new List<string>();
        var seen = new HashSet<int>();
        foreach (var t in body.Tracks)
        {
            int? trackId = null;
            if (!string.IsNullOrWhiteSpace(t.FilePath) && byPath.TryGetValue(t.FilePath.Trim(), out var byP))
                trackId = byP;
            else if (!string.IsNullOrWhiteSpace(t.Title)
                     && byTag.TryGetValue($"{(t.Artist ?? "").Trim().ToLowerInvariant()}|{t.Title.Trim().ToLowerInvariant()}", out var byT))
                trackId = byT;

            if (trackId is int tid)
            {
                if (!seen.Add(tid)) continue;   // dup in the file → keep the first
                pl.Tracks.Add(new SavedPlaylistTrack { TrackId = tid, Position = pos++ });
            }
            else
            {
                missing.Add(string.IsNullOrWhiteSpace(t.Artist) ? t.Title ?? "(untitled)" : $"{t.Artist} – {t.Title}");
            }
        }

        db.SavedPlaylists.Add(pl);
        await db.SaveChangesAsync(ct);
        return Ok(new
        {
            pl.Id,
            pl.Name,
            matched = pos,
            missing = missing.Count,
            missingSamples = missing.Take(10).ToList()
        });
    }

    // ── Membership ────────────────────────────────────────────────────────────

    /// <summary>The playlist's tracks in order, with the display columns the
    /// admin grid shows.</summary>
    [HttpGet("{id:int}/tracks")]
    public async Task<IActionResult> Tracks(int id, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        if (!await db.SavedPlaylists.AnyAsync(p => p.Id == id, ct)) return NotFound();

        var items = await db.SavedPlaylistTracks.AsNoTracking()
            .Where(t => t.SavedPlaylistId == id)
            .OrderBy(t => t.Position)
            .Select(t => new
            {
                EntryId = t.Id,
                t.Position,
                t.TrackId,
                t.Track!.Title,
                t.Track!.Artist,
                t.Track!.DurationSec,
                t.Track!.Bpm,
                Lufs = t.Track!.LufsIntegrated,
                t.Track!.Type
            })
            .ToListAsync(ct);
        return Ok(new { items });
    }

    /// <summary>Appends a track (dup-tolerant: adding a track already in the
    /// playlist is a no-op that reports <c>alreadyPresent</c>).</summary>
    [HttpPost("{id:int}/tracks")]
    public async Task<IActionResult> AddTrack(int id, [FromBody] AddTrackBody? body, CancellationToken ct)
    {
        if (body is null) return UnprocessableEntity(new { error = "trackId is required" });

        await using var db = await _dbf.CreateDbContextAsync(ct);
        if (!await db.SavedPlaylists.AnyAsync(p => p.Id == id, ct)) return NotFound();
        if (!await db.Tracks.AnyAsync(t => t.Id == body.TrackId, ct))
            return NotFound(new { error = "track not found", body.TrackId });

        if (await db.SavedPlaylistTracks.AnyAsync(t => t.SavedPlaylistId == id && t.TrackId == body.TrackId, ct))
            return Ok(new { added = false, alreadyPresent = true });

        int pos = await db.SavedPlaylistTracks
            .Where(t => t.SavedPlaylistId == id)
            .Select(t => (int?)t.Position)
            .MaxAsync(ct) is int m ? m + 1 : 0;

        db.SavedPlaylistTracks.Add(new SavedPlaylistTrack
        {
            SavedPlaylistId = id,
            TrackId = body.TrackId,
            Position = pos,
            AddedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        return Ok(new { added = true, position = pos });
    }

    [HttpDelete("{id:int}/tracks/{entryId:int}")]
    public async Task<IActionResult> RemoveTrack(int id, int entryId, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var entry = await db.SavedPlaylistTracks
            .FirstOrDefaultAsync(t => t.Id == entryId && t.SavedPlaylistId == id, ct);
        if (entry == null) return NotFound();

        db.SavedPlaylistTracks.Remove(entry);
        await db.SaveChangesAsync(ct);

        // Renumber to contiguous 0..n-1.
        var rest = await db.SavedPlaylistTracks
            .Where(t => t.SavedPlaylistId == id)
            .OrderBy(t => t.Position).ToListAsync(ct);
        for (int i = 0; i < rest.Count; i++) rest[i].Position = i;
        await db.SaveChangesAsync(ct);
        return Ok(new { removed = true });
    }

    // ── Schedule slots (replace-all, ≤5, mirroring the retired category slots) ─

    [HttpGet("{id:int}/slots")]
    public async Task<IActionResult> Slots(int id, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        if (!await db.SavedPlaylists.AnyAsync(p => p.Id == id, ct)) return NotFound();

        var slots = await db.SavedPlaylistSlots.AsNoTracking()
            .Where(s => s.SavedPlaylistId == id)
            .OrderBy(s => s.SlotIndex)
            .Select(s => new SlotBody(s.SlotIndex, s.Enabled, s.TimeFromHHmm, s.TimeToHHmm, s.DaysMask))
            .ToListAsync(ct);
        return Ok(new { slots });
    }

    [HttpPut("{id:int}/slots")]
    public async Task<IActionResult> PutSlots(int id, [FromBody] List<SlotBody>? body, CancellationToken ct)
    {
        body ??= new List<SlotBody>();
        if (body.Count > SavedPlaylist.MaxSlots)
            return UnprocessableEntity(new { error = $"maximum {SavedPlaylist.MaxSlots} slots" });
        if (body.Select(s => s.SlotIndex).Distinct().Count() != body.Count
            || body.Any(s => s.SlotIndex is < 0 or >= SavedPlaylist.MaxSlots))
            return UnprocessableEntity(new { error = "slot indexes must be unique, 0..4" });

        await using var db = await _dbf.CreateDbContextAsync(ct);
        if (!await db.SavedPlaylists.AnyAsync(p => p.Id == id, ct)) return NotFound();

        var existing = await db.SavedPlaylistSlots.Where(s => s.SavedPlaylistId == id).ToListAsync(ct);
        db.SavedPlaylistSlots.RemoveRange(existing);
        foreach (var s in body)
            db.SavedPlaylistSlots.Add(new SavedPlaylistSlot
            {
                SavedPlaylistId = id,
                SlotIndex = s.SlotIndex,
                Enabled = s.Enabled,
                TimeFromHHmm = s.TimeFrom,
                TimeToHHmm = s.TimeTo,
                DaysMask = s.DaysMask
            });
        await db.SaveChangesAsync(ct);
        return Ok(new { count = body.Count });
    }
}
