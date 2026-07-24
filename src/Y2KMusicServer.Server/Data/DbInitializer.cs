using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Data.Entities;

namespace Y2KMusicServer.Server.Data;

/// <summary>
/// Database setup. Creates the schema directly from the model
/// (<c>EnsureCreated</c>, no migrations) and seeds the singleton
/// <see cref="Settings"/> row when absent. Idempotent per startup.
///
/// <para><b>Schema v2 recreate rule:</b> the library rework (categories
/// retired, saved playlists added, Track reshaped) changed the schema, and
/// EnsureCreated cannot evolve an existing database. A database from the old
/// schema — detected by the presence of a <c>Categories</c> table or the
/// absence of the v2 <c>SavedPlaylists</c> table — is <b>deleted and rebuilt
/// empty</b>, together with the per-track caches keyed by the old track ids
/// (peaks, structure) and the web download cache. Accepted during development;
/// the library is repopulated by a rescan of the global folder list.</para>
/// </summary>
public static class DbInitializer
{
    public static void Initialize(IServiceProvider services, IConfiguration cfg)
    {
        var factory = services.GetRequiredService<IDbContextFactory<Y2KDbContext>>();
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("DbInitializer");

        RecreateIfOldSchema(cfg, log);

        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();

        SeedSettings(db, cfg);

        db.SaveChanges();
    }

    /// <summary>
    /// Deletes a pre-v2 database (plus the track-id-keyed caches and the web
    /// download cache) so EnsureCreated can build the current schema. A missing
    /// or already-v2 database is left alone.
    /// </summary>
    private static void RecreateIfOldSchema(IConfiguration cfg, ILogger log)
    {
        var dbPath = DataPaths.DbPath(cfg);
        if (!File.Exists(dbPath)) return;

        bool oldSchema;
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT " +
                "  EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='Categories')," +
                "  EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='SavedPlaylists')," +
                // v2.1: the silence-bound columns (EnsureCreated cannot add
                // columns, so their absence forces the same rebuild).
                "  EXISTS(SELECT 1 FROM pragma_table_info('Tracks') WHERE name='LeadInSec')";
            using var r = cmd.ExecuteReader();
            r.Read();
            bool hasCategories = r.GetInt64(0) != 0;
            bool hasSavedPlaylists = r.GetInt64(1) != 0;
            bool hasLeadIn = r.GetInt64(2) != 0;
            oldSchema = hasCategories || !hasSavedPlaylists || !hasLeadIn;
        }
        catch (Exception ex)
        {
            // Unreadable database — treat as old/corrupt and rebuild.
            log.LogWarning(ex, "Could not inspect the database schema; rebuilding.");
            oldSchema = true;
        }

        if (!oldSchema) return;

        log.LogWarning(
            "Pre-v2.1 database detected at {Path} — deleting it (and the track-keyed " +
            "caches) for the library rework. The library repopulates on the next scan.",
            dbPath);

        SqliteConnection.ClearAllPools(); // release file handles before deleting
        TryDelete(dbPath, log);
        TryDelete(dbPath + "-wal", log);
        TryDelete(dbPath + "-shm", log);

        // Track-id-keyed caches are meaningless against a fresh id space.
        TryDeleteDirContents(DataPaths.PeaksDir(cfg), log);
        TryDeleteDirContents(DataPaths.StructureDir(cfg), log);

        // Web-cache files' Track rows died with the database; without them the
        // files are unreachable orphans — drop them too (rebuildable by re-fetch).
        TryDeleteDirContents(DataPaths.WebCacheDir(cfg), log);
    }

    private static void TryDelete(string path, ILogger log)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { log.LogWarning(ex, "Could not delete {Path}", path); }
    }

    private static void TryDeleteDirContents(string dir, ILogger log)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir))
                try { File.Delete(f); } catch (Exception ex) { log.LogWarning(ex, "Could not delete {Path}", f); }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not clean {Dir}", dir);
        }
    }

    private static void SeedSettings(Y2KDbContext db, IConfiguration cfg)
    {
        if (db.Settings.Any()) return;

        // The install-time default LUFS lives in appsettings (Defaults:TargetLufs);
        // fall back to the legacy -18 target if it is unset.
        var targetLufs = cfg.GetValue<double?>("Defaults:TargetLufs") ?? -18.0;

        // Defaults match the legacy WinForms app's fresh-install state.
        db.Settings.Add(new Settings
        {
            Id = 1,
            SmartMix = true,
            SmartBeatFader = false,
            NextTriggerPct = 50,
            NextFadeSeconds = 6,
            AutoDj = true,
            AutoDjTracks = 3,
            AutoDjBpmDev = 5,
            ScanWorkers = 4,
            NormalizeEnabled = true,
            LimiterEnabled = true,
            TargetLufs = targetLufs,
            Volume = 80,
            StreamingBitrate = 128,
            AllowWebNext = false,
            ShowWebCategories = true,
            DebugLogging = false,
            UpdateUrl = null
        });
    }
}
