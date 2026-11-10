using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Data;
using Y2KMusicServer.Server.Integrations;

namespace Y2KMusicServer.Server.Scanning;

/// <summary>
/// Re-homes the library when the music has moved — a new drive letter, a new
/// machine, a new share — by rewriting the path prefix of every affected
/// <c>Track.FilePath</c> IN PLACE.
///
/// The point is that track ids do not change. Saved playlists, the live queue,
/// requests, the mix cache, the shuffle history, jingles and the analysis
/// columns all key on the id, so they carry over untouched. The alternative —
/// add the new folder and rescan — creates a second row per song with a new id,
/// and every playlist goes on pointing at the old, now-unplayable rows.
///
/// The JSON stores that hold paths are moved with it: the folder list, the
/// YouTube download index, and the YouTube folder setting when it sits under
/// the moved location.
///
/// If the new location was already scanned (so a song exists twice: the old row
/// with the history, the new row from the rescan), the two are MERGED onto the
/// old row: its playlist / queue / request references are kept, the duplicate's
/// are moved onto it, analysis the old row lacks is taken from the duplicate,
/// and the duplicate row is deleted.
/// </summary>
public static class FolderRelocation
{
    /// <summary>What a relocation would do (preview) or did (applied).</summary>
    public sealed record Result(
        string From,
        string To,
        bool TargetExists,
        int Tracks,
        int Folders,
        int YouTubeEntries,
        bool YouTubeFolder,
        int Duplicates,
        int Sampled,
        int SampleFound,
        bool Applied,
        string? Error);

    /// <summary>How many moved files are spot-checked at the new location. Enough
    /// to tell "right folder" from "wrong folder" without walking 18 000 files
    /// over a network share.</summary>
    private const int SampleSize = 200;

    /// <summary>Id lists are applied in slices so no single statement carries an
    /// unbounded IN list.</summary>
    private const int SliceSize = 500;

    public static string Normalise(string? p)
        => (p ?? "").Trim().Trim('"').Trim().TrimEnd('\\', '/');

    /// <summary>
    /// Previews (<paramref name="apply"/> false) or performs the move. A preview
    /// never writes anything. The caller is responsible for cancelling scans and
    /// analysis before an apply.
    /// </summary>
    public static async Task<Result> RunAsync(IDbContextFactory<Y2KDbContext> dbf, IConfiguration cfg,
        string? fromRaw, string? toRaw, bool apply, ILogger log, CancellationToken ct)
    {
        var from = Normalise(fromRaw);
        var to = Normalise(toRaw);
        var error = Validate(from, to);
        bool targetExists = error == null && SafeDirectoryExists(to);
        var fromPrefix = FolderScope.Prefix(from);

        // ── Plan ────────────────────────────────────────────────────────────
        List<(int Id, string NewPath)> moves = new();
        List<(int Keep, int Drop)> dupes = new();
        int folders = 0, ytEntries = 0;
        bool ytFolder = false;

        if (error == null)
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var all = await db.Tracks.AsNoTracking()
                .Select(t => new { t.Id, t.FilePath })
                .ToListAsync(ct);

            // Case-insensitive, like the scanner's own matching on Windows paths.
            var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in all) byPath.TryAdd(t.FilePath, t.Id);

            foreach (var t in all)
            {
                if (!t.FilePath.StartsWith(fromPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                var newPath = to + "\\" + t.FilePath.Substring(fromPrefix.Length);
                moves.Add((t.Id, newPath));
                if (byPath.TryGetValue(newPath, out var other) && other != t.Id)
                    dupes.Add((t.Id, other));
            }

            folders = ScanFolderStore.Load(cfg).Folders.Count(f => IsAtOrUnder(f.Path, from));
            ytEntries = YouTubeDownloadIndex.Load(cfg).Entries.Count(
                e => e.FilePath.StartsWith(fromPrefix, StringComparison.OrdinalIgnoreCase));
            var ytStored = IntegrationsStore.Load(cfg).DownloadFolder;
            ytFolder = !string.IsNullOrWhiteSpace(ytStored) && IsAtOrUnder(Normalise(ytStored), from);

            if (moves.Count == 0 && folders == 0 && ytEntries == 0 && !ytFolder)
                error = $"Nothing in the library lives under {from}.";
        }

        // Spot-check: do the files really sit at the new location? Evenly spaced
        // so one missing subfolder can't hide behind a lucky first 200.
        int sampled = 0, found = 0;
        if (error == null && targetExists && moves.Count > 0)
        {
            int step = Math.Max(1, moves.Count / SampleSize);
            for (int i = 0; i < moves.Count && sampled < SampleSize; i += step)
            {
                sampled++;
                if (SafeFileExists(moves[i].NewPath)) found++;
            }
        }

        if (error == null && apply && !targetExists)
            error = $"{to} does not exist on this machine. Connect the drive or share first.";

        Result Make(bool applied, string? err) => new(from, to, targetExists, moves.Count, folders,
            ytEntries, ytFolder, dupes.Count, sampled, found, applied, err);

        if (error != null || !apply) return Make(false, error);

        // ── Apply ───────────────────────────────────────────────────────────
        await using (var db = await dbf.CreateDbContextAsync(ct))
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // Duplicates first: the unique index on FilePath means the duplicate
            // row must be gone before the original can take its path.
            if (dupes.Count > 0) await MergeDuplicatesAsync(db, dupes, ct);

            var newPathById = moves.ToDictionary(m => m.Id, m => m.NewPath);
            foreach (var slice in newPathById.Keys.Chunk(SliceSize))
            {
                var ids = slice.ToList();
                var rows = await db.Tracks.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
                foreach (var r in rows) r.FilePath = newPathById[r.Id];
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }

            await tx.CommitAsync(ct);
        }

        // The database is the part that must be atomic; the JSON stores follow.
        int foldersMoved = ScanFolderStore.Relocate(cfg, from, to);
        int ytMoved = YouTubeDownloadIndex.Relocate(cfg, fromPrefix, to);
        if (ytFolder)
        {
            var c = IntegrationsStore.Load(cfg);
            var stored = Normalise(c.DownloadFolder);
            c.DownloadFolder = string.Equals(stored, from, StringComparison.OrdinalIgnoreCase)
                ? to
                : to + "\\" + stored.Substring(fromPrefix.Length);
            IntegrationsStore.Save(cfg, c);
        }

        // The duplicates' per-track caches belong to rows that no longer exist.
        foreach (var (_, drop) in dupes)
        {
            TryDelete(Path.Combine(DataPaths.PeaksDir(cfg), drop + ".json"));
            TryDelete(Path.Combine(DataPaths.StructureDir(cfg), drop + ".json"));
        }

        log.LogInformation(
            "Relocated {From} -> {To}: {Tracks} track(s), {Folders} folder(s), {Yt} YouTube record(s), " +
            "{Dupes} duplicate(s) merged{YtFolder}.",
            from, to, moves.Count, foldersMoved, ytMoved, dupes.Count,
            ytFolder ? ", YouTube folder moved" : "");

        return Make(true, null);
    }

    /// <summary>
    /// Folds each duplicate row (<c>Drop</c>, found at the new location by a
    /// rescan) into the original (<c>Keep</c>, the row the playlists point at).
    /// </summary>
    private static async Task MergeDuplicatesAsync(Y2KDbContext db, List<(int Keep, int Drop)> dupes,
        CancellationToken ct)
    {
        var keepOf = dupes.ToDictionary(d => d.Drop, d => d.Keep);

        foreach (var slice in keepOf.Keys.Chunk(SliceSize))
        {
            var dropIds = slice.ToList();

            // Saved playlists: move the membership onto the original, unless the
            // playlist already has the original — then the duplicate entry goes.
            var memberships = await db.SavedPlaylistTracks
                .Where(x => dropIds.Contains(x.TrackId)).ToListAsync(ct);
            foreach (var m in memberships)
            {
                int keep = keepOf[m.TrackId];
                bool already = await db.SavedPlaylistTracks.AsNoTracking()
                    .AnyAsync(x => x.SavedPlaylistId == m.SavedPlaylistId && x.TrackId == keep, ct);
                if (already) db.SavedPlaylistTracks.Remove(m);
                else m.TrackId = keep;
            }

            foreach (var e in await db.PlaylistEntries.Where(x => dropIds.Contains(x.TrackId)).ToListAsync(ct))
                e.TrackId = keepOf[e.TrackId];
            foreach (var r in await db.Requests.Where(x => dropIds.Contains(x.TrackId)).ToListAsync(ct))
                r.TrackId = keepOf[r.TrackId];

            // Analysis the original never got (its file was unreachable) can come
            // from the duplicate, which was analysed at the new location.
            var keepIds = dropIds.Select(d => keepOf[d]).ToList();
            var keeps = await db.Tracks.Where(t => keepIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
            var drops = await db.Tracks.Where(t => dropIds.Contains(t.Id)).ToListAsync(ct);
            foreach (var d in drops)
            {
                if (!keeps.TryGetValue(keepOf[d.Id], out var k)) continue;
                if (k.Bpm == null && d.Bpm != null)
                {
                    k.Bpm = d.Bpm;
                    k.BpmConfidence = d.BpmConfidence;
                    k.BeatPhaseOffsetSec = d.BeatPhaseOffsetSec;
                }
                k.LufsIntegrated ??= d.LufsIntegrated;
                k.LeadInSec ??= d.LeadInSec;
                k.LeadOutSec ??= d.LeadOutSec;
            }
            await db.SaveChangesAsync(ct);

            // Mix-cache FKs are Restrict, so the duplicate's pairs go explicitly.
            await db.MixCache
                .Where(m => dropIds.Contains(m.FromTrackId) || dropIds.Contains(m.ToTrackId))
                .ExecuteDeleteAsync(ct);

            db.Tracks.RemoveRange(drops);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>Null when the pair is usable, else a message for the operator.</summary>
    private static string? Validate(string from, string to)
    {
        if (from.Length == 0) return "Enter the old location.";
        if (to.Length == 0) return "Enter the new location.";
        if (!Path.IsPathRooted(from) || !Path.IsPathRooted(to))
            return "Both locations must be full paths, like D:\\Music or \\\\server\\share\\Music.";
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return "The old and new locations are the same.";

        var fp = FolderScope.Prefix(from);
        var tp = FolderScope.Prefix(to);
        if (tp.StartsWith(fp, StringComparison.OrdinalIgnoreCase) || fp.StartsWith(tp, StringComparison.OrdinalIgnoreCase))
            return "One location can't be inside the other.";
        return null;
    }

    private static bool IsAtOrUnder(string path, string folder)
        => string.Equals(path.TrimEnd('\\', '/'), folder, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(FolderScope.Prefix(folder), StringComparison.OrdinalIgnoreCase);

    private static bool SafeDirectoryExists(string p) { try { return Directory.Exists(p); } catch { return false; } }
    private static bool SafeFileExists(string p) { try { return File.Exists(p); } catch { return false; } }
    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
}
