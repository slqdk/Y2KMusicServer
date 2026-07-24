using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Data;
using Y2KMusicServer.Server.Data.Entities;
using Y2KMusicServer.Server.Network;

namespace Y2KMusicServer.Server.Scanning;

/// <summary>
/// Walks the global scan-folder list (<see cref="ScanFolderStore"/>), reads
/// tags + duration, and writes <see cref="Track"/> rows. Tags only — BPM/LUFS
/// arrive via the analysis pass. Files already in the library are skipped;
/// with categories retired there is no per-category ownership, only the
/// "innermost assigned folder wins" scoping used by folder-scoped operations.
///
/// The scanner holds no ASP.NET dependency — it raises <see cref="Progress"/>
/// events; <c>ScanHubBroadcaster</c> forwards them to the SignalR hub.
/// </summary>
public sealed class LibraryScanner
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".flac" };

    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly NetworkShareConnector _connector;
    private readonly IConfiguration _cfg;
    private readonly ILogger<LibraryScanner> _log;

    private readonly object _gate = new();
    private volatile bool _abort;
    private readonly Queue<int?> _queue = new(); // folder id, or null = all folders
    private bool _busy; // worker loop active (draining the queue)
    private volatile ScanProgress _current = new() { State = ScanState.Idle };

    public LibraryScanner(IDbContextFactory<Y2KDbContext> dbf, NetworkShareConnector connector,
        IConfiguration cfg, ILogger<LibraryScanner> log)
    {
        _dbf = dbf;
        _connector = connector;
        _cfg = cfg;
        _log = log;
    }

    /// <summary>Raised whenever progress changes. Subscribers must not throw.</summary>
    public event Action<ScanProgress>? Progress;

    public ScanProgress Current => _current;
    public bool IsScanning { get { lock (_gate) return _busy; } }

    /// <summary>
    /// Queues a scan of the whole global folder list, or of a single assigned
    /// folder by its <see cref="ScanFolderStore"/> id (its sub-tree, minus any
    /// deeper assigned folder — "innermost folder wins"; the chained analysis
    /// pass is scoped to just that folder's tracks). Scans run one at a time in
    /// FIFO order; an identical request already waiting is coalesced. Always
    /// returns true (the request is accepted, to run now or shortly).
    /// </summary>
    public bool TryStart(int? folderId = null)
    {
        lock (_gate)
        {
            if (!_queue.Contains(folderId))
                _queue.Enqueue(folderId);
            if (_busy) return true; // a worker is already draining the queue
            _busy = true;
        }

        _ = Task.Run(DrainQueue);
        return true;
    }

    /// <summary>
    /// Aborts the running scan (nothing read so far is persisted — a partial
    /// scan of a removed folder must not add ghost tracks) and drops queued
    /// scans. Used when folders are removed or cleared mid-scan.
    /// </summary>
    public void CancelAll()
    {
        _abort = true;
        lock (_gate) _queue.Clear();
    }

    private void DrainQueue()
    {
        while (true)
        {
            int? folderId;
            lock (_gate)
            {
                if (_queue.Count == 0) { _busy = false; return; }
                folderId = _queue.Dequeue();
            }

            try
            {
                Run(folderId);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Library scan failed");
                Emit(new ScanProgress { State = ScanState.Failed, Message = ex.Message });
            }
        }
    }

    private void Run(int? folderId)
    {
        _abort = false; // a fresh scan clears any previous cancellation
        Emit(new ScanProgress { State = ScanState.Enumerating });

        // (folderPath, nested-folder exclusion prefixes) per target — from the
        // global scan-folder store, not the database. Folders nested inside a
        // target are scanned on their own, not here (innermost wins).
        var store = ScanFolderStore.Load(_cfg);
        var allFolders = store.Folders.Select(f => f.Path).ToList();

        List<(string Folder, List<string> Exclude)> targets;
        if (folderId is int fid)
        {
            var folder = store.Folders.FirstOrDefault(f => f.Id == fid);
            if (folder == null)
            {
                Emit(new ScanProgress { State = ScanState.Completed, Message = "Folder not found.", ScopeFolderId = folderId });
                return;
            }
            targets = new() { (folder.Path, FolderScope.NestedPrefixes(folder.Path, allFolders)) };
        }
        else
        {
            targets = store.Folders
                .Select(f => (f.Path, FolderScope.NestedPrefixes(f.Path, allFolders)))
                .ToList();
        }

        HashSet<string> existing;
        using (var db = _dbf.CreateDbContext())
        {
            existing = db.Tracks
                .Select(t => t.FilePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        // Build the worklist. `claimed` starts from the existing library so
        // already-scanned files are skipped, and grows as folders claim files,
        // giving first-wins for any remaining cross-folder dupes.
        var claimed = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var work = new List<string>();

        foreach (var (folder, exclude) in targets)
        {
            // Authenticate to the server first if this is a network folder (using
            // the stored credential for its host). No-op for local folders or
            // hosts with no stored credential; a failure falls through to the
            // enumerate try/catch below as a skipped folder.
            if (OperatingSystem.IsWindows())
                _connector.EnsureConnected(folder);

            IEnumerable<string> files;
            try
            {
                files = Directory
                    .EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not enumerate folder {Folder}", folder);
                continue;
            }

            foreach (var file in files)
            {
                // Innermost wins: a file under a deeper assigned folder belongs to
                // that folder's scan, not this one.
                if (exclude.Count > 0 && exclude.Any(p => file.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (claimed.Add(file)) // Add returns false when already present
                    work.Add(file);
            }
        }

        var found = work.Count;
        if (found == 0)
        {
            Emit(new ScanProgress
            {
                State = ScanState.Completed,
                Message = "No new files found.",
                ScopeFolderId = folderId
            });
            return;
        }

        Emit(new ScanProgress { State = ScanState.Scanning, FilesFound = found });

        var processed = 0;
        var added = 0;
        var skipped = 0;
        var workers = ReadScanWorkers();
        var results = new ConcurrentBag<Track>();

        Parallel.ForEach(
            work,
            new ParallelOptions { MaxDegreeOfParallelism = workers },
            item =>
            {
                if (_abort) return; // folder removed mid-scan — stop reading

                var track = ReadTrack(item);
                if (track != null)
                {
                    results.Add(track);
                    Interlocked.Increment(ref added);
                }
                else
                {
                    Interlocked.Increment(ref skipped);
                }

                var done = Interlocked.Increment(ref processed);
                if (done % 25 == 0 || done == found)
                    Emit(new ScanProgress
                    {
                        State = ScanState.Scanning,
                        FilesFound = found,
                        FilesProcessed = done,
                        Added = Volatile.Read(ref added),
                        Skipped = Volatile.Read(ref skipped),
                        CurrentPath = item
                    });
            });

        if (_abort)
        {
            // Discard everything: persisting a partial read of a folder that was
            // just removed would resurrect its tracks as ghosts.
            Emit(new ScanProgress
            {
                State = ScanState.Completed,
                FilesFound = found,
                FilesProcessed = processed,
                Message = "Cancelled (folders changed); nothing was added.",
                ScopeFolderId = folderId
            });
            return;
        }

        // Persist in batches to bound memory on large libraries.
        using (var db = _dbf.CreateDbContext())
        {
            const int batchSize = 500;
            var all = results.ToList();
            for (var i = 0; i < all.Count; i += batchSize)
            {
                db.Tracks.AddRange(all.GetRange(i, Math.Min(batchSize, all.Count - i)));
                db.SaveChanges();
            }
        }

        Emit(new ScanProgress
        {
            State = ScanState.Completed,
            FilesFound = found,
            FilesProcessed = processed,
            Added = added,
            Skipped = skipped,
            Message = $"Added {added}, skipped {skipped}.",
            ScopeFolderId = folderId
        });
    }

    private Track? ReadTrack(string path)
    {
        try
        {
            string? title = null, artist = null, album = null, genre = null;
            int? year = null;
            double duration = 0;

            try
            {
                using var tf = TagLib.File.Create(path);
                title = NullIfBlank(tf.Tag.Title);
                artist = NullIfBlank(tf.Tag.FirstPerformer) ?? NullIfBlank(tf.Tag.FirstAlbumArtist);
                album = NullIfBlank(tf.Tag.Album);
                genre = NullIfBlank(tf.Tag.FirstGenre);
                if (tf.Tag.Year > 0) year = (int)tf.Tag.Year;
                duration = tf.Properties?.Duration.TotalSeconds ?? 0;
            }
            catch (Exception ex)
            {
                // Unreadable/corrupt tags: still index the file with a
                // filename-derived title so it isn't silently lost.
                _log.LogDebug(ex, "Tag read failed for {Path}; storing minimal entry", path);
            }

            return new Track
            {
                FilePath = path,
                Title = title ?? Path.GetFileNameWithoutExtension(path),
                Artist = artist,
                Album = album,
                Genre = genre,
                Year = year,
                Type = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
                DurationSec = duration,
                ScannedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read {Path}", path);
            return null;
        }
    }

    private int ReadScanWorkers()
    {
        try
        {
            using var db = _dbf.CreateDbContext();
            var w = db.Settings.AsNoTracking().FirstOrDefault()?.ScanWorkers ?? 4;
            return Math.Max(1, Math.Min(w, 32));
        }
        catch
        {
            return 4;
        }
    }

    private void Emit(ScanProgress p)
    {
        int queued;
        lock (_gate) queued = _queue.Count;
        p = p with { Queued = queued };
        _current = p;
        Progress?.Invoke(p);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
