using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Integrations;
using Y2KMusicServer.Server.Updates;

namespace Y2KMusicServer.Server.Data;

/// <summary>
/// Moves a whole instance between machines: the library database (tracks with
/// their analysis, saved playlists, schedules, the queue, requests, the mix
/// cache, settings) and every JSON store beside it (folders, genre map, mix
/// rules, web config, Auto DJ feeds, jingles, playhead, shuffle memory, cast
/// speakers, YouTube settings and index).
///
/// EXPORT is a single zip:
/// <code>
///   manifest.json      what it is, where it came from, which folders it uses
///   data/y2k.db        consistent online snapshot (SQLite backup API)
///   stores/*.json      every JSON store in the data folder, minus the
///                      machine-bound and transient ones (below)
///   files/…            the YouTube downloads, but only when they live inside
///                      the data folder (the default) — otherwise they sit with
///                      the music and move with it
/// </code>
/// Left out: <c>network-shares.json</c> (passwords encrypted with THIS machine's
/// key — unreadable anywhere else), <c>youtube-queue.json</c> (in-flight jobs),
/// and the rebuildable caches (waveform peaks, structure, web cache, logs).
///
/// IMPORT replaces the running data, which cannot be done while the database is
/// open and singletons hold store contents in memory. So it is two-phase: the
/// upload is verified and unpacked into <c>import-pending\</c>, the operator
/// confirms, the service restarts, and <see cref="ApplyPendingImport"/> swaps
/// the files in at startup — before anything opens them. The data it replaces
/// is kept in <c>before-import-&lt;stamp&gt;\</c>, never deleted.
///
/// Paths inside the import still name the OLD machine's folders. That is
/// deliberate: the Music folders dialog shows them as unreachable, and
/// Relocate re-homes them without changing a single track id.
/// </summary>
public static class InstanceBackup
{
    public const string Format = "y2k-instance-export";
    public const int FormatVersion = 1;

    private const string PendingDirName = "import-pending";
    private const string ReadyMarker = "import.ready";
    private const string WorkDirName = "backup-tmp";

    /// <summary>Stores never exported, and never replaced by an import.</summary>
    private static readonly HashSet<string> MachineBound = new(StringComparer.OrdinalIgnoreCase)
    {
        "network-shares.json",   // DPAPI LocalMachine — only this machine can read it
        "youtube-queue.json",    // in-flight download jobs, not state worth moving
    };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public sealed class Manifest
    {
        public string Format { get; set; } = "";
        public int FormatVersion { get; set; }
        public string AppVersion { get; set; } = "";
        public DateTime ExportedUtc { get; set; }
        public string Machine { get; set; } = "";
        public string DataPath { get; set; } = "";
        public int Tracks { get; set; }
        public int Playlists { get; set; }
        public List<string> Folders { get; set; } = new();
        public string YouTubeFolder { get; set; } = "";
        public bool IncludesYouTubeFiles { get; set; }
        public List<string> Stores { get; set; } = new();
    }

    /// <summary>What an uploaded export contains, for the confirm step.</summary>
    public sealed record StagedFolder(string Path, bool FoundHere);
    public sealed record StagedImport(Manifest Manifest, List<StagedFolder> Folders, int YouTubeFiles);

    // ── Export ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the export zip in the data folder's scratch area and returns its
    /// path. The caller streams it and deletes it.
    /// </summary>
    public static async Task<string> ExportAsync(IDbContextFactory<Y2KDbContext> dbf, IConfiguration cfg,
        CancellationToken ct)
    {
        var dataDir = Path.GetFullPath(DataPaths.DataDir(cfg));
        var work = PrepareWorkDir(cfg);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
        var zipPath = Path.Combine(work, $"y2k-export-{San(Environment.MachineName)}-{stamp}.zip");
        var dbCopy = Path.Combine(work, $"snapshot-{Guid.NewGuid():N}.db");

        var manifest = new Manifest
        {
            Format = Format,
            FormatVersion = FormatVersion,
            AppVersion = GitHubUpdateChecker.CurrentVersion(),
            ExportedUtc = DateTime.UtcNow,
            Machine = Environment.MachineName,
            DataPath = dataDir,
            Folders = ScanFolderStore.AllPaths(cfg),
        };

        try
        {
            // The SQLite online backup copies a consistent snapshot while the
            // server keeps running — no need to stop playback to export.
            await using (var db = await dbf.CreateDbContextAsync(ct))
            {
                manifest.Tracks = await db.Tracks.CountAsync(ct);
                manifest.Playlists = await db.SavedPlaylists.CountAsync(ct);

                using var src = new SqliteConnection(db.Database.GetConnectionString());
                src.Open();
                using var dst = new SqliteConnection($"Data Source={dbCopy};Pooling=False");
                dst.Open();
                src.BackupDatabase(dst);
            }

            // YouTube downloads travel only when they live inside the data folder.
            var ytFolder = Path.GetFullPath(YouTubeFolder(cfg));
            manifest.YouTubeFolder = ytFolder;
            var ytFiles = IsUnder(ytFolder, dataDir) && Directory.Exists(ytFolder)
                ? Directory.GetFiles(ytFolder, "*", SearchOption.AllDirectories)
                : Array.Empty<string>();
            manifest.IncludesYouTubeFiles = ytFiles.Length > 0;

            var stores = Directory.GetFiles(dataDir, "*.json", SearchOption.TopDirectoryOnly)
                .Where(f => !MachineBound.Contains(Path.GetFileName(f)))
                .ToList();
            manifest.Stores = stores.Select(f => Path.GetFileName(f)).ToList();

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var me = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using (var w = me.Open())
                    await JsonSerializer.SerializeAsync(w, manifest, Indented, ct);

                zip.CreateEntryFromFile(dbCopy, "data/y2k.db", CompressionLevel.Optimal);

                foreach (var s in stores)
                    AddShared(zip, s, "stores/" + Path.GetFileName(s));

                foreach (var f in ytFiles)
                {
                    var rel = Path.GetRelativePath(dataDir, f).Replace('\\', '/');
                    AddShared(zip, f, "files/" + rel, CompressionLevel.NoCompression);
                }
            }
            return zipPath;
        }
        catch
        {
            TryDeleteFile(zipPath);
            throw;
        }
        finally
        {
            TryDeleteFile(dbCopy);
        }
    }

    // ── Import, phase 1: verify and stage ───────────────────────────────────

    /// <summary>
    /// Verifies an uploaded export and unpacks it into <c>import-pending\</c>.
    /// Nothing live is touched. Throws <see cref="InvalidDataException"/> with an
    /// operator-readable message when the file can't be used.
    /// </summary>
    public static async Task<StagedImport> StageAsync(Stream upload, IDbContextFactory<Y2KDbContext> dbf,
        IConfiguration cfg, CancellationToken ct)
    {
        var dataDir = Path.GetFullPath(DataPaths.DataDir(cfg));
        var work = PrepareWorkDir(cfg);
        var zipPath = Path.Combine(work, $"upload-{Guid.NewGuid():N}.zip");
        var pending = Path.Combine(dataDir, PendingDirName);

        try
        {
            await using (var fs = File.Create(zipPath))
                await upload.CopyToAsync(fs, ct);

            using var zip = OpenZip(zipPath);

            // What is it?
            var me = zip.GetEntry("manifest.json")
                ?? throw new InvalidDataException("That file is not a Y2K Music Server export (no manifest).");
            Manifest manifest;
            await using (var s = me.Open())
                manifest = await JsonSerializer.DeserializeAsync<Manifest>(s, cancellationToken: ct)
                           ?? throw new InvalidDataException("The export's manifest is unreadable.");
            if (manifest.Format != Format)
                throw new InvalidDataException("That file is not a Y2K Music Server export.");
            if (manifest.FormatVersion > FormatVersion)
                throw new InvalidDataException(
                    $"The export was made by a newer version ({manifest.AppVersion}). Update this install first.");

            var dbEntry = zip.GetEntry("data/y2k.db")
                ?? throw new InvalidDataException("The export has no library database.");

            // Start from an empty staging folder — a previous half-finished
            // upload must not leak files into this one.
            TryDeleteDir(pending);
            Directory.CreateDirectory(Path.Combine(pending, "data"));
            Directory.CreateDirectory(Path.Combine(pending, "stores"));
            var stagedDb = Path.Combine(pending, "data", "y2k.db");
            dbEntry.ExtractToFile(stagedDb, overwrite: true);

            // Will this install be able to open it? There are no migrations, so
            // the export must come from a build with exactly this schema.
            await using (var db = await dbf.CreateDbContextAsync(ct))
            {
                var problem = CheckSchema(stagedDb, db, manifest.AppVersion);
                if (problem != null) throw new InvalidDataException(problem);
            }

            var filesRoot = Path.GetFullPath(Path.Combine(pending, "files"));
            int ytFiles = 0;
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/')) continue;   // directory entry

                if (e.FullName.StartsWith("stores/", StringComparison.Ordinal))
                {
                    var name = e.FullName.Substring("stores/".Length);
                    // A plain *.json file name — nothing that could climb out.
                    if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains("..")
                        || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        || MachineBound.Contains(name))
                        continue;
                    e.ExtractToFile(Path.Combine(pending, "stores", name), overwrite: true);
                }
                else if (e.FullName.StartsWith("files/", StringComparison.Ordinal))
                {
                    var target = Path.GetFullPath(Path.Combine(filesRoot,
                        e.FullName.Substring("files/".Length).Replace('/', Path.DirectorySeparatorChar)));
                    if (!IsUnder(target, filesRoot)) continue;   // zip-slip guard
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    e.ExtractToFile(target, overwrite: true);
                    ytFiles++;
                }
            }

            await File.WriteAllTextAsync(Path.Combine(pending, "manifest.json"),
                JsonSerializer.Serialize(manifest, Indented), ct);

            var folders = manifest.Folders
                .Select(f => new StagedFolder(f, SafeDirectoryExists(f)))
                .ToList();
            return new StagedImport(manifest, folders, ytFiles);
        }
        catch
        {
            TryDeleteDir(pending);
            throw;
        }
        finally
        {
            TryDeleteFile(zipPath);
        }
    }

    /// <summary>The staged-but-unconfirmed import, if any (the dialog reopens).</summary>
    public static StagedImport? Pending(IConfiguration cfg)
    {
        var pending = Path.Combine(DataPaths.DataDir(cfg), PendingDirName);
        var mf = Path.Combine(pending, "manifest.json");
        if (!File.Exists(mf) || !File.Exists(Path.Combine(pending, "data", "y2k.db"))) return null;
        try
        {
            var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(mf));
            if (m == null) return null;
            var filesDir = Path.Combine(pending, "files");
            int yt = Directory.Exists(filesDir)
                ? Directory.GetFiles(filesDir, "*", SearchOption.AllDirectories).Length : 0;
            return new StagedImport(m, m.Folders.Select(f => new StagedFolder(f, SafeDirectoryExists(f))).ToList(), yt);
        }
        catch { return null; }
    }

    public static void DiscardPending(IConfiguration cfg)
        => TryDeleteDir(Path.Combine(DataPaths.DataDir(cfg), PendingDirName));

    // ── Import, phase 2: confirm, restart, swap at startup ──────────────────

    /// <summary>Marks the staged import as confirmed. The next start applies it.</summary>
    public static bool Confirm(IConfiguration cfg)
    {
        if (Pending(cfg) == null) return false;
        File.WriteAllText(Path.Combine(DataPaths.DataDir(cfg), PendingDirName, ReadyMarker),
            DateTime.UtcNow.ToString("O"));
        return true;
    }

    /// <summary>
    /// Restarts the Windows service so the confirmed import is applied. A clean
    /// stop is not a "failure", so the installer's restart-on-failure recovery
    /// would not bring the service back; instead a detached PowerShell helper
    /// waits for the stop to finish and starts it again. Returns false when not
    /// running as a service (console / F5) — restart by hand there.
    /// </summary>
    public static bool ScheduleServiceRestart(IHostApplicationLifetime life, ILogger log)
    {
        if (!OperatingSystem.IsWindows()
            || !Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
            return false;

        // Same service name the installer and the tray use.
        const string script =
            "$n='Y2KMusicServer'; Start-Sleep -Seconds 2; " +
            "for($i=0; $i -lt 90; $i++){ if((Get-Service $n).Status -eq 'Stopped'){ break }; Start-Sleep -Seconds 1 }; " +
            "Start-Service $n";
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not start the restart helper; the service must be restarted by hand.");
            return false;
        }

        // Let the HTTP reply reach the browser before the host goes down.
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            log.LogInformation("Stopping for import; the service will start again on its own.");
            life.StopApplication();
        });
        return true;
    }

    /// <summary>
    /// Called at startup BEFORE the host is built — nothing has the database or
    /// the stores open yet. Applies a confirmed import, or discards an
    /// unconfirmed one. Never throws: on failure the previous data is put back
    /// and the service starts as it was.
    /// </summary>
    public static void ApplyPendingImport(IConfiguration cfg)
    {
        var dataDir = Path.GetFullPath(DataPaths.DataDir(cfg));
        var pending = Path.Combine(dataDir, PendingDirName);
        if (!Directory.Exists(pending)) return;

        if (!File.Exists(Path.Combine(pending, ReadyMarker)))
        {
            // Uploaded but never confirmed: a restart for any other reason must
            // not silently replace the library.
            Serilog.Log.Information("Import: discarding an unconfirmed staged import.");
            TryDeleteDir(pending);
            return;
        }

        var stagedDb = Path.Combine(pending, "data", "y2k.db");
        if (!File.Exists(stagedDb))
        {
            Serilog.Log.Warning("Import: staged import has no database; discarded.");
            TryDeleteDir(pending);
            return;
        }

        var backup = Path.Combine(dataDir, "before-import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var dbPath = Path.GetFullPath(DataPaths.DbPath(cfg));
        var movedAside = new List<(string Original, string Saved)>();

        try
        {
            Directory.CreateDirectory(Path.Combine(backup, "data"));

            // 1. The current data goes aside — never deleted.
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                var f = dbPath + suffix;
                if (!File.Exists(f)) continue;
                var saved = Path.Combine(backup, "data", Path.GetFileName(f));
                File.Move(f, saved);
                movedAside.Add((f, saved));
            }
            foreach (var f in Directory.GetFiles(dataDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                if (MachineBound.Contains(Path.GetFileName(f))) continue;
                var saved = Path.Combine(backup, Path.GetFileName(f));
                File.Move(f, saved);
                movedAside.Add((f, saved));
            }

            // 2. The import comes in.
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            File.Copy(stagedDb, dbPath, overwrite: true);

            var stores = Path.Combine(pending, "stores");
            if (Directory.Exists(stores))
                foreach (var f in Directory.GetFiles(stores, "*.json"))
                    if (!MachineBound.Contains(Path.GetFileName(f)))
                        File.Copy(f, Path.Combine(dataDir, Path.GetFileName(f)), overwrite: true);

            var files = Path.Combine(pending, "files");
            if (Directory.Exists(files))
                foreach (var f in Directory.GetFiles(files, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(dataDir, Path.GetRelativePath(files, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(f, target, overwrite: true);
                }

            // 3. Per-track caches are keyed by track id, and the ids now belong to
            //    the imported library — anything cached here describes other songs.
            TryClearDir(DataPaths.PeaksDir(cfg));
            TryClearDir(DataPaths.StructureDir(cfg));

            TryDeleteDir(pending);
            Serilog.Log.Information(
                "Import applied. The previous data is kept in {Backup}. Open Music folders to re-home " +
                "any folder that lives somewhere else on this machine.", backup);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Import failed; putting the previous data back.");
            try
            {
                TryDeleteFile(dbPath);
                foreach (var (original, saved) in movedAside)
                    if (File.Exists(saved)) File.Move(saved, original, overwrite: true);
            }
            catch (Exception rex)
            {
                Serilog.Log.Error(rex, "Import rollback incomplete — the previous data is in {Backup}.", backup);
            }
            TryDeleteDir(pending);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Null when the database matches this build's model exactly, else why not.
    /// Compares every mapped table's columns: a missing column means the export
    /// is older than this install, an unknown one that it is newer.
    /// </summary>
    private static string? CheckSchema(string dbFile, Y2KDbContext db, string exportVersion)
    {
        var mine = GitHubUpdateChecker.CurrentVersion();
        using var conn = new SqliteConnection($"Data Source={dbFile};Mode=ReadOnly;Pooling=False");
        try { conn.Open(); }
        catch { return "The library database in the export is damaged."; }

        foreach (var table in db.Model.GetRelationalModel().Tables)
        {
            var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                var quoted = table.Name.Replace("\"", "\"\"");
                cmd.CommandText = "PRAGMA table_info(\"" + quoted + "\")";
                using var r = cmd.ExecuteReader();
                while (r.Read()) actual.Add(r.GetString(1));
            }

            var expected = table.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (actual.Count == 0 || expected.Except(actual).Any())
                return $"The export comes from an older version ({exportVersion}) than this install ({mine}). " +
                       "Update the old install, export again, and import that.";
            if (actual.Except(expected).Any())
                return $"The export comes from a newer version ({exportVersion}) than this install ({mine}). " +
                       "Update this install first.";
        }
        return null;
    }

    private static string YouTubeFolder(IConfiguration cfg)
    {
        var configured = IntegrationsStore.Load(cfg).DownloadFolder;
        return string.IsNullOrWhiteSpace(configured) ? DataPaths.YouTubeDownloadDir(cfg) : configured.Trim();
    }

    /// <summary>The scratch folder, emptied of anything older than an hour (an
    /// export the browser never finished downloading, a crashed upload).</summary>
    private static string PrepareWorkDir(IConfiguration cfg)
    {
        var dir = Path.Combine(DataPaths.DataDir(cfg), WorkDirName);
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir))
            try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-1)) File.Delete(f); } catch { }
        return dir;
    }

    /// <summary>Adds a file that another part of the server may hold open
    /// (stores are rewritten in place), by reading it with shared access.</summary>
    private static void AddShared(ZipArchive zip, string file, string entryName,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        try
        {
            using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var e = zip.CreateEntry(entryName, level);
            using var dst = e.Open();
            src.CopyTo(dst);
        }
        catch (IOException) { /* vanished mid-export; the rest is still worth having */ }
    }

    private static ZipArchive OpenZip(string path)
    {
        try { return ZipFile.OpenRead(path); }
        catch (InvalidDataException) { throw new InvalidDataException("That file is not a zip."); }
    }

    private static bool IsUnder(string path, string root)
    {
        var r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private static string San(string s) => string.Concat(s.Where(c => char.IsLetterOrDigit(c) || c == '-'));
    private static bool SafeDirectoryExists(string p) { try { return Directory.Exists(p); } catch { return false; } }
    private static void TryDeleteFile(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
    private static void TryDeleteDir(string p) { try { if (Directory.Exists(p)) Directory.Delete(p, true); } catch { } }

    private static void TryClearDir(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir)) TryDeleteFile(f);
        }
        catch { }
    }
}
