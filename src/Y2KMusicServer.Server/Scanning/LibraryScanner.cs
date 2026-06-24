using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Data;
using Y2KMusicServer.Server.Data.Entities;

namespace Y2KMusicServer.Server.Scanning;

/// <summary>
/// Walks each category's folders, reads tags + duration, and writes
/// <see cref="Track"/> rows. A straight port of the legacy scan: tags only,
/// no BPM or LUFS analysis (those arrive in Phase 5). Files already in the
/// library are skipped; a file claimed by an earlier category (by
/// <c>DisplayOrder</c>) wins, matching the legacy first-wins behaviour.
///
/// The scanner holds no ASP.NET dependency — it raises <see cref="Progress"/>
/// events; <c>ScanHubBroadcaster</c> forwards them to the SignalR hub.
/// </summary>
public sealed class LibraryScanner
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".flac" };

    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly ILogger<LibraryScanner> _log;

    private int _running; // 0 = idle, 1 = scanning
    private volatile ScanProgress _current = new() { State = ScanState.Idle };

    public LibraryScanner(IDbContextFactory<Y2KDbContext> dbf, ILogger<LibraryScanner> log)
    {
        _dbf = dbf;
        _log = log;
    }

    /// <summary>Raised whenever progress changes. Subscribers must not throw.</summary>
    public event Action<ScanProgress>? Progress;

    public ScanProgress Current => _current;
    public bool IsScanning => Volatile.Read(ref _running) == 1;

    /// <summary>
    /// Starts a scan on a background thread. Pass a category id to scan just
    /// that category, or null to scan every category that has folders. Returns
    /// false if a scan is already running.
    /// </summary>
    public bool TryStart(int? categoryId = null)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return false;

        _ = Task.Run(() =>
        {
            try
            {
                Run(categoryId);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Library scan failed");
                Emit(new ScanProgress { State = ScanState.Failed, Message = ex.Message });
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });

        return true;
    }

    private void Run(int? categoryId)
    {
        Emit(new ScanProgress { State = ScanState.Enumerating });

        List<(int CategoryId, string[] Folders)> targets;
        HashSet<string> existing;

        using (var db = _dbf.CreateDbContext())
        {
            var cats = db.Categories
                .Include(c => c.Folders)
                .Where(c => c.Folders.Count > 0)
                .Where(c => categoryId == null || c.Id == categoryId)
                .OrderBy(c => c.DisplayOrder)
                .ToList();

            targets = cats
                .Select(c => (c.Id, c.Folders.Select(f => f.Path).ToArray()))
                .ToList();

            existing = db.Tracks
                .Select(t => t.FilePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        // Build the worklist. `claimed` starts from the existing library so
        // already-scanned files are skipped, and grows as categories claim
        // files in DisplayOrder, giving first-wins for cross-category dupes.
        var claimed = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var work = new List<(int CategoryId, string Path)>();

        foreach (var (catId, folders) in targets)
        {
            foreach (var folder in folders)
            {
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
                    if (claimed.Add(file)) // Add returns false when already present
                        work.Add((catId, file));
            }
        }

        var found = work.Count;
        if (found == 0)
        {
            Emit(new ScanProgress
            {
                State = ScanState.Completed,
                Message = "No new files found."
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
                var track = ReadTrack(item.Path, item.CategoryId);
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
                        CurrentPath = item.Path
                    });
            });

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
            Message = $"Added {added}, skipped {skipped}."
        });
    }

    private Track? ReadTrack(string path, int categoryId)
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
                CategoryId = categoryId,
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
        _current = p;
        Progress?.Invoke(p);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
