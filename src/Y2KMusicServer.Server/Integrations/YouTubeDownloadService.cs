using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Audio;
using Y2KMusicServer.Server.Data;
using Y2KMusicServer.Server.Data.Entities;
using Y2KMusicServer.Server.Network;

namespace Y2KMusicServer.Server.Integrations;

/// <summary>
/// One job as it is written to <c>youtube-queue.json</c>. A public, plain type
/// rather than the internal job object: the queue file outlives the process, so
/// its shape is a contract and must not drift with an internal refactor.
/// Percentage is deliberately absent — a resumed job restarts from zero.
/// </summary>
public sealed class YouTubeDownloadRecord
{
    public int Id { get; set; }
    public string VideoId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Artist { get; set; }
    public string State { get; set; } = "queued";
    public string? Message { get; set; }
    public int? TrackId { get; set; }
    public string? FilePath { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>The queue file itself: every job, pending and historical.</summary>
public sealed class YouTubeQueueFile
{
    public List<YouTubeDownloadRecord> Jobs { get; set; } = new();
}

/// <summary>One queued / running / finished download, as the admin and DJ
/// consoles see it.</summary>
public sealed record YouTubeDownloadDto(
    int Id, string VideoId, string Title, string? Artist, string State,
    double Percent, string? Message, int? TrackId, string? FilePath);

/// <summary>
/// The "paste a link, get the song in the library" path.
///
/// Pasted links (or plain words) become jobs on an in-memory queue that ONE
/// worker drains, so a batch never floods the network or the disk while the
/// decks are reading. Each job runs yt-dlp into a local temp folder, embeds
/// tags + cover art, checks the result actually decodes, moves the finished MP3
/// into the operator's YouTube folder under an <c>Artist - Title.mp3</c> name,
/// indexes it as an ordinary <see cref="Track"/>, and kicks the normal
/// missing-only analysis pass so tempo and silence bounds land like any scanned
/// track.
///
/// The YouTube folder is deliberately NOT one of the Music folders: nothing in
/// the folder list owns it, so a folder Clear / Remove can never prune these
/// tracks, and they never show up as part of a scanned collection. They are
/// still ordinary library rows — searchable, queueable, mixable.
///
/// Downloading happens in a temp folder on the local disk even when the target
/// is an SMB share: yt-dlp and ffmpeg then never write partial files or do
/// random-access transcoding over the network, and only the finished file
/// crosses the wire.
/// </summary>
public sealed class YouTubeDownloadService : BackgroundService
{
    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly IConfiguration _cfg;
    private readonly AudioAnalysisService _analysis;
    private readonly NetworkShareConnector _connector;
    private readonly ILogger<YouTubeDownloadService> _log;

    private readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>();
    private readonly List<Job> _jobs = new();
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();
    private int _nextId;

    /// <summary>Finished jobs kept for the console's list; older ones drop off.</summary>
    private const int HistoryLimit = 60;

    /// <summary>Ceiling on how many tracks one pasted playlist / album link may
    /// expand to, so a stray "mix" link can't enqueue a thousand downloads.</summary>
    private const int MaxExpand = 100;

    private static readonly Regex VideoId = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);
    private static readonly Regex LinkVideoId = new(
        @"(?:[?&]v=|youtu\.be/|/shorts/|/embed/|/live/)([A-Za-z0-9_-]{11})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PercentLine = new(@"Y2KPCT\s*([0-9]+(?:\.[0-9]+)?)%", RegexOptions.Compiled);

    public YouTubeDownloadService(IDbContextFactory<Y2KDbContext> dbf, IConfiguration cfg,
                                  AudioAnalysisService analysis, NetworkShareConnector connector,
                                  ILogger<YouTubeDownloadService> log)
    {
        _dbf = dbf;
        _cfg = cfg;
        _analysis = analysis;
        _connector = connector;
        _log = log;
    }

    // ── Job model ──────────────────────────────────────────────────────────

    private sealed class Job
    {
        public int Id;
        public string VideoId = "";
        public string Title = "";
        public string? Artist;
        public string State = "queued";   // queued | downloading | indexing | done | failed | cancelled
        public double Percent;
        public string? Message;
        public int? TrackId;
        public string? FilePath;
        public DateTime CreatedUtc = DateTime.UtcNow;

        public YouTubeDownloadDto ToDto()
            => new(Id, VideoId, Title, Artist, State, Percent, Message, TrackId, FilePath);

        public YouTubeDownloadRecord ToRecord() => new()
        {
            Id = Id, VideoId = VideoId, Title = Title, Artist = Artist,
            State = State, Message = Message, TrackId = TrackId,
            FilePath = FilePath, CreatedUtc = CreatedUtc
        };

        public static Job FromRecord(YouTubeDownloadRecord r) => new()
        {
            Id = r.Id, VideoId = r.VideoId, Title = r.Title, Artist = r.Artist,
            State = r.State, Message = r.Message, TrackId = r.TrackId,
            FilePath = r.FilePath, CreatedUtc = r.CreatedUtc,
            Percent = r.State == "done" ? 100 : 0
        };
    }

    // ── Public surface ─────────────────────────────────────────────────────

    /// <summary>Every job, newest first.</summary>
    public IReadOnlyList<YouTubeDownloadDto> Jobs()
    {
        lock (_gate)
            return _jobs.OrderByDescending(j => j.Id).Select(j => j.ToDto()).ToList();
    }

    /// <summary>True while anything is queued or running.</summary>
    public bool Busy
    {
        get { lock (_gate) return _jobs.Any(j => j.State is "queued" or "downloading" or "indexing"); }
    }

    /// <summary>
    /// Turns pasted text into jobs. One entry per line (or per whitespace-separated
    /// URL): a video link or bare id becomes one job; a playlist / album link
    /// expands to its tracks (capped); anything else is treated as search words
    /// and takes YouTube's first hit — which is what makes the phone console
    /// usable without typing a URL.
    /// </summary>
    public async Task<(int Queued, string? Error)> EnqueueAsync(string text, CancellationToken ct)
    {
        var folderErr = ValidateFolder();
        if (folderErr != null) return (0, folderErr);

        var lines = (text ?? "")
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(l => l.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? l.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : new[] { l })
            .Select(l => l.Trim('"', '\'', '<', '>'))
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count == 0) return (0, "Nothing to download.");

        int queued = 0;
        string? lastError = null;

        foreach (var line in lines)
        {
            if (ct.IsCancellationRequested) break;

            var m = LinkVideoId.Match(line);
            if (m.Success) { Add(m.Groups[1].Value, line); queued++; continue; }
            if (VideoId.IsMatch(line)) { Add(line, line); queued++; continue; }

            bool isUrl = line.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                      || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            // A playlist / album link expands; plain words take the first hit.
            var (ids, err) = await ResolveIdsAsync(isUrl ? line : $"ytsearch1:{line}",
                                                   isUrl ? MaxExpand : 1, ct);
            if (ids.Count == 0)
            {
                lastError = err ?? $"Nothing found for \"{Shorten(line)}\".";
                continue;
            }
            foreach (var (id, title) in ids) { Add(id, line, title); queued++; }
        }

        if (queued == 0) return (0, lastError ?? "Nothing could be resolved.");
        return (queued, lastError);

        void Add(string videoId, string source, string? title = null)
        {
            var job = new Job
            {
                Id = Interlocked.Increment(ref _nextId),
                VideoId = videoId,
                Title = title ?? Shorten(source),
                State = "queued"
            };
            lock (_gate)
            {
                _jobs.Add(job);
                Trim();
            }
            _queue.Writer.TryWrite(job);
            SaveQueue();
        }
    }

    /// <summary>Cancels a queued or running job. Finished jobs are left alone.</summary>
    public bool Cancel(int id)
    {
        if (_running.TryGetValue(id, out var cts))
        {
            try { cts.Cancel(); } catch { /* already gone */ }
            return true;
        }
        lock (_gate)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job == null || job.State != "queued") return false;
            job.State = "cancelled";
            job.Message = "Cancelled before it started.";
        }
        SaveQueue();
        return true;
    }

    /// <summary>Drops finished / failed / cancelled jobs from the list.</summary>
    public int ClearFinished()
    {
        int removed;
        lock (_gate)
        {
            int before = _jobs.Count;
            _jobs.RemoveAll(j => j.State is "done" or "failed" or "cancelled");
            removed = before - _jobs.Count;
        }
        SaveQueue();
        return removed;
    }

    /// <summary>The configured target folder, or the default under the data
    /// directory when unset.</summary>
    public string TargetFolder()
    {
        var configured = IntegrationsStore.Load(_cfg).DownloadFolder;
        return string.IsNullOrWhiteSpace(configured)
            ? DataPaths.YouTubeDownloadDir(_cfg)
            : configured.Trim();
    }

    /// <summary>The reason downloads can't run at all, or null. Only a folder that
    /// can't be resolved stops a job — where the operator puts it is their call.</summary>
    public string? ValidateFolder()
    {
        var folder = TargetFolder();
        if (folder.Length == 0) return "No YouTube folder is set (Music folders → YouTube downloads).";
        return null;
    }

    /// <summary>
    /// An advisory note about the chosen folder, or null. Putting the YouTube
    /// folder inside a Music folder is allowed and sometimes wanted — the
    /// downloads then get rescanned and cleared along with that collection — but
    /// it does mean that folder's Clear button prunes them, so the console says
    /// so rather than deciding for the operator.
    /// </summary>
    public string? FolderNote()
    {
        var folder = TargetFolder();
        if (folder.Length == 0) return null;

        var self = FolderScope.Prefix(folder);
        foreach (var scan in ScanFolderStore.AllPaths(_cfg))
        {
            var pre = FolderScope.Prefix(scan);
            if (self.StartsWith(pre, StringComparison.OrdinalIgnoreCase))
                return $"Inside the Music folder \"{scan}\" — its Clear button will prune these downloads too.";
            if (pre.StartsWith(self, StringComparison.OrdinalIgnoreCase))
                return $"The Music folder \"{scan}\" sits inside this one, so scans will cover the downloads as well.";
        }
        return null;
    }

    // ── Surviving a restart ────────────────────────────────────────────────

    /// <summary>
    /// Reloads the queue written by the last run. Anything that was mid-flight
    /// when the service stopped (a restart, a crash, a reboot) goes back to
    /// "queued" and is downloaded again from the start — a half-written file in
    /// the scratch folder is deleted before the retry, so a resumed job can't
    /// produce a truncated track. Finished rows are kept as history so the
    /// console still shows what happened before the restart.
    /// </summary>
    private void RestoreQueue()
    {
        List<Job> resume = new();
        try
        {
            var path = DataPaths.YouTubeQueuePath(_cfg);
            if (!File.Exists(path)) return;

            var file = JsonSerializer.Deserialize<YouTubeQueueFile>(File.ReadAllText(path));
            var records = file?.Jobs ?? new List<YouTubeDownloadRecord>();
            if (records.Count == 0) return;

            lock (_gate)
            {
                foreach (var r in records.OrderBy(r => r.Id))
                {
                    var job = Job.FromRecord(r);
                    if (job.State is "queued" or "downloading" or "indexing")
                    {
                        job.State = "queued";
                        job.Percent = 0;
                        job.Message = "Queued again after a restart.";
                        resume.Add(job);
                    }
                    _jobs.Add(job);
                    if (job.Id > _nextId) _nextId = job.Id;
                }
                Trim();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not restore the YouTube download queue");
            return;
        }

        foreach (var job in resume) _queue.Writer.TryWrite(job);
        if (resume.Count > 0)
            _log.LogInformation("YouTube downloads: resumed {Count} unfinished job(s) after restart",
                resume.Count);
        SaveQueue();
    }

    /// <summary>
    /// Writes the queue to disk. Called on every state change EXCEPT percentage
    /// ticks — those move several times a second and would hammer the disk for
    /// nothing, since a resumed job restarts from zero anyway.
    /// </summary>
    private void SaveQueue()
    {
        try
        {
            List<YouTubeDownloadRecord> records;
            lock (_gate) records = _jobs.OrderBy(j => j.Id).Select(j => j.ToRecord()).ToList();

            var path = DataPaths.YouTubeQueuePath(_cfg);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path,
                JsonSerializer.Serialize(new YouTubeQueueFile { Jobs = records },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not persist the YouTube download queue");
        }
    }

    // ── Worker ─────────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RestoreQueue();

        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            lock (_gate) { if (job.State == "cancelled") continue; }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _running[job.Id] = cts;
            try
            {
                await RunJobAsync(job, cts.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                Set(job, "cancelled", msg: "Cancelled.");
            }
            catch (OperationCanceledException)
            {
                Set(job, "cancelled", msg: "Service stopping.");
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "YouTube download job {Id} ({VideoId}) failed", job.Id, job.VideoId);
                Set(job, "failed", msg: ex.Message);
            }
            finally
            {
                _running.TryRemove(job.Id, out _);
            }
        }
    }

    private async Task RunJobAsync(Job job, CancellationToken ct)
    {
        Set(job, "downloading", pct: 0, msg: "Starting…");

        var folder = TargetFolder();
        var folderErr = ValidateFolder();
        if (folderErr != null) { Set(job, "failed", msg: folderErr); return; }

        // A UNC target needs the stored share credentials before anything is
        // written — the service is LocalSystem and has no session of its own.
        if (OperatingSystem.IsWindows())
        {
            var root = NetworkShareConnector.ShareRoot(folder);
            if (root != null) _connector.EnsureConnected(root);
        }
        Directory.CreateDirectory(folder);

        // Already downloaded once and still on disk + in the library → nothing to do.
        var known = YouTubeDownloadIndex.Find(_cfg, job.VideoId);
        if (known != null && File.Exists(known.FilePath))
        {
            await using var db = await _dbf.CreateDbContextAsync(ct);
            var row = await db.Tracks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.FilePath == known.FilePath, ct);
            if (row != null)
            {
                job.Title = row.Title ?? job.Title;
                job.Artist = row.Artist;
                job.FilePath = row.FilePath;
                job.TrackId = row.Id;
                Set(job, "done", pct: 100, msg: "Already in the library.");
                return;
            }
        }

        var tmpDir = DataPaths.EnsureYouTubeTempDir(_cfg);
        foreach (var stale in Directory.EnumerateFiles(tmpDir, job.VideoId + ".*"))
            try { File.Delete(stale); } catch { /* a leftover lock is not fatal */ }

        var ytDlp = _cfg["Integrations:YouTube:YtDlpPath"] ?? "yt-dlp";
        var ffmpeg = _cfg["Integrations:YouTube:FfmpegPath"] ?? "ffmpeg";

        var tmpFile = Path.Combine(tmpDir, job.VideoId + ".mp3");
        var stderr = new List<string>();

        // YouTube refuses some player clients' media URLs while still handing out
        // metadata — a clean resolve followed by "HTTP Error 403: Forbidden". The
        // escape is to ask a different client for the URLs, so the default attempt
        // is followed by each configured fallback until one delivers a file. A
        // failure that doesn't look like a block (missing binary, video removed)
        // stops the loop immediately rather than burning four attempts on it.
        var attempts = YouTubeToolArgs.ClientAttempts(_cfg);
        for (int i = 0; i < attempts.Count; i++)
        {
            var client = attempts[i];
            stderr.Clear();

            var args = new List<string>
            {
                "-x", "--audio-format", "mp3", "--audio-quality", "0",
                "--embed-metadata",                  // title / artist / album / date into ID3
                "--embed-thumbnail",                 // cover art into ID3 (APIC)
                "--convert-thumbnails", "jpg",       // WebP art is not universally readable
                "--no-playlist", "--no-warnings", "--newline",
                "--progress-template", "download:Y2KPCT %(progress._percent_str)s",
                "-o", Path.Combine(tmpDir, "%(id)s.%(ext)s")
            };
            // yt-dlp finds ffmpeg on PATH, which for a LocalSystem service is the
            // machine PATH — point it at the configured binary when we have one.
            if (ffmpeg.IndexOfAny(new[] { '\\', '/' }) >= 0)
            {
                args.Add("--ffmpeg-location");
                args.Add(ffmpeg);
            }
            args.AddRange(YouTubeToolArgs.Common(_cfg));          // cookies + operator args
            args.AddRange(YouTubeToolArgs.ClientArg(client));     // blank on the first pass
            // A stale cached player/signature can itself cause a 403, so retries
            // ignore the cache rather than replay whatever failed the first time.
            if (i > 0) args.Add("--no-cache-dir");
            args.Add($"https://www.youtube.com/watch?v={job.VideoId}");

            var label = client.Length == 0 ? "" : $" ({client})";
            Set(job, "downloading", pct: 0, msg: $"Downloading…{label}");

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(15));
                try
                {
                    await RunStreamedAsync(ytDlp, args, line =>
                    {
                        var m = PercentLine.Match(line);
                        if (m.Success && double.TryParse(m.Groups[1].Value,
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var pct))
                        {
                            Set(job, "downloading", pct: Math.Clamp(pct, 0, 100), msg: $"Downloading…{label}");
                        }
                        else if (line.Contains("[ExtractAudio]", StringComparison.Ordinal))
                            Set(job, "downloading", pct: 100, msg: "Converting to MP3…");
                        else if (line.Contains("[ThumbnailsConvertor]", StringComparison.Ordinal)
                              || line.Contains("[EmbedThumbnail]", StringComparison.Ordinal))
                            Set(job, "downloading", pct: 100, msg: "Adding cover art…");
                    }, stderr, timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Set(job, "failed", msg: "Download timed out after 15 minutes.");
                    return;
                }
            }

            if (File.Exists(tmpFile))
            {
                if (i > 0)
                    _log.LogInformation("YouTube download: {VideoId} needed player client \"{Client}\"",
                        job.VideoId, client);
                break;
            }

            List<string> snapshot;
            lock (stderr) snapshot = stderr.ToList();
            if (!YouTubeToolArgs.LooksBlocked(snapshot)) break;   // not a block — retrying won't help

            // Leave nothing half-written behind before the next client tries.
            foreach (var partial in Directory.EnumerateFiles(tmpDir, job.VideoId + ".*"))
                try { File.Delete(partial); } catch { }
        }

        if (!File.Exists(tmpFile))
        {
            List<string> snapshot;
            lock (stderr) snapshot = stderr.ToList();
            var why = snapshot.FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
            if (YouTubeToolArgs.LooksBlocked(snapshot))
                why = "YouTube refused the download (403). Update yt-dlp, or add a cookies file "
                    + "in Settings → YouTube integration.";
            Set(job, "failed", msg: string.IsNullOrEmpty(why) ? "Download produced no file." : why);
            return;
        }

        Set(job, "indexing", pct: 100, msg: "Checking the file…");

        // Tags, read the same way the scanner reads them.
        string? title = null, artist = null, album = null, genre = null;
        int? year = null;
        double duration = 0;
        try
        {
            using var tf = TagLib.File.Create(tmpFile);
            title = NullIfBlank(tf.Tag.Title);
            artist = NullIfBlank(tf.Tag.FirstPerformer) ?? NullIfBlank(tf.Tag.FirstAlbumArtist);
            album = NullIfBlank(tf.Tag.Album);
            genre = NullIfBlank(tf.Tag.FirstGenre);
            if (tf.Tag.Year > 0) year = (int)tf.Tag.Year;
            duration = tf.Properties?.Duration.TotalSeconds ?? 0;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Tag read failed for {Path}; using minimal metadata", tmpFile);
        }

        // Playability gate: the loudness analyser decodes the whole file through
        // the same reader the engine plays with, so an empty result means the file
        // would not play. Drop it rather than let a dud into the library.
        double? lufs = null;
        try { lufs = new LoudnessAnalyzer().AnalyzeFile(tmpFile); } catch { /* → null */ }
        if (lufs == null)
        {
            try { File.Delete(tmpFile); } catch { }
            Set(job, "failed", msg: "The downloaded file could not be decoded — discarded.");
            return;
        }

        // Move into the YouTube folder under a readable name.
        Set(job, "indexing", pct: 100, msg: "Filing it…");
        var baseName = BuildName(artist, title, job.VideoId);
        var finalPath = UniquePath(folder, baseName, ".mp3");
        try
        {
            File.Move(tmpFile, finalPath);
        }
        catch (Exception ex)
        {
            Set(job, "failed", msg: $"Could not write to {folder}: {ex.Message}");
            return;
        }

        // Index it as an ordinary track (no category — Auto DJ never auto-picks it;
        // it plays when queued or when added to a saved playlist).
        int trackId;
        await using (var db = await _dbf.CreateDbContextAsync(ct))
        {
            var existing = await db.Tracks.FirstOrDefaultAsync(t => t.FilePath == finalPath, ct);
            var track = existing ?? new Track { FilePath = finalPath };
            track.Title = title ?? Path.GetFileNameWithoutExtension(finalPath);
            track.Artist = artist;
            track.Album = album;
            track.Genre = genre;
            track.Year = year;
            track.Type = "MP3";
            track.DurationSec = duration;
            track.LufsIntegrated = lufs;
            track.ScannedAt = DateTime.UtcNow;
            if (existing == null) db.Tracks.Add(track);
            await db.SaveChangesAsync(ct);
            trackId = track.Id;
        }

        YouTubeDownloadIndex.Record(_cfg, job.VideoId, finalPath, title, artist);

        job.Title = title ?? job.Title;
        job.Artist = artist;
        job.FilePath = finalPath;
        job.TrackId = trackId;
        Set(job, "done", pct: 100, msg: "In the library.");

        _log.LogInformation("YouTube download: \"{Title}\" ({VideoId}) → {Path} as track {TrackId}",
            job.Title, job.VideoId, finalPath, trackId);

        // Tempo + silence bounds come from the normal missing-only analysis pass,
        // exactly as they would for a scanned file. Loudness is already stored, so
        // the track is level-matched even before that pass gets to it.
        try { _analysis.TryStart(reanalyzeAll: false); } catch { /* a busy pass queues itself */ }
    }

    // ── yt-dlp helpers ─────────────────────────────────────────────────────

    /// <summary>Flat-resolves a target (playlist / album URL or a search) to video
    /// ids. Never throws: a bad document yields an empty list and a message.</summary>
    private async Task<(List<(string Id, string Title)> Ids, string? Error)> ResolveIdsAsync(
        string target, int max, CancellationToken ct)
    {
        var ytDlp = _cfg["Integrations:YouTube:YtDlpPath"] ?? "yt-dlp";
        var found = new List<(string, string)>();
        var stdout = new List<string>();
        var stderr = new List<string>();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await RunStreamedAsync(ytDlp,
                new[] { "--flat-playlist", "-J", "--no-warnings", target },
                stdout.Add, stderr, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (found, "Timed out looking that up.");
        }

        var json = string.Join("\n", stdout).Trim();
        if (json.Length == 0)
            return (found, stderr.FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "yt-dlp returned nothing.");

        try
        {
            using var doc = JsonDocument.Parse(json);
            // yt-dlp prints a bare `null` when an extractor produced no info dict.
            if (doc.RootElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return (found, "That link returned no tracks.");
            Collect(doc.RootElement, found, max, 0);
        }
        catch (JsonException)
        {
            return (found, "yt-dlp returned something unreadable.");
        }
        return (found, found.Count == 0 ? "No tracks in that link." : null);
    }

    private static void Collect(JsonElement el, List<(string, string)> found, int max, int depth)
    {
        if (found.Count >= max || depth > 4) return;

        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in el.EnumerateArray())
            {
                Collect(child, found, max, depth + 1);
                if (found.Count >= max) return;
            }
            return;
        }
        if (el.ValueKind != JsonValueKind.Object) return;

        if (el.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in entries.EnumerateArray())
            {
                Collect(child, found, max, depth + 1);
                if (found.Count >= max) return;
            }
            return;
        }
        if (Str(el, "_type") == "playlist") return;

        var id = Str(el, "id");
        if (id is null || !VideoId.IsMatch(id)) return;
        if (found.Any(f => f.Item1 == id)) return;
        found.Add((id, Str(el, "title") ?? id));
    }

    /// <summary>Runs a tool, handing every stdout line to <paramref name="onLine"/>
    /// as it arrives (that's what makes live progress possible) and collecting
    /// stderr for the error message.</summary>
    private static async Task RunStreamedAsync(
        string exe, IEnumerable<string> args, Action<string> onLine,
        List<string> stderr, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (Exception ex)
        {
            stderr.Add($"Not found or not runnable ({exe}): {ex.Message}");
            return;
        }

        var errTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync()) != null)
                lock (stderr) stderr.Add(line);
        }, CancellationToken.None);

        try
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct)) != null)
                onLine(line);
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        finally
        {
            try { await errTask; } catch { /* best effort */ }
        }
    }

    // ── small helpers ──────────────────────────────────────────────────────

    private void Set(Job job, string state, double? pct = null, string? msg = null)
    {
        bool stateChanged;
        lock (_gate)
        {
            stateChanged = job.State != state;
            job.State = state;
            if (pct is double p) job.Percent = p;
            if (msg != null) job.Message = msg;
        }
        // Only a state change is worth a disk write; percentage ticks are not.
        if (stateChanged) SaveQueue();
    }

    private void Trim()
    {
        if (_jobs.Count <= HistoryLimit) return;
        var finished = _jobs.Where(j => j.State is "done" or "failed" or "cancelled")
                            .OrderBy(j => j.Id).ToList();
        foreach (var j in finished)
        {
            if (_jobs.Count <= HistoryLimit) break;
            _jobs.Remove(j);
        }
    }

    /// <summary>"Artist - Title", or just the title, or the video id — sanitised
    /// for the file system and length-capped.</summary>
    private static string BuildName(string? artist, string? title, string videoId)
    {
        var t = (title ?? "").Trim();
        var a = (artist ?? "").Trim();
        var name = a.Length > 0 && t.Length > 0 ? $"{a} - {t}"
                 : t.Length > 0 ? t
                 : videoId;

        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
        name = Regex.Replace(name, @"\s+", " ").Trim().Trim('.');
        if (name.Length > 120) name = name[..120].Trim();
        return name.Length == 0 ? videoId : name;
    }

    /// <summary>A free path in the folder: "name.mp3", then "name (2).mp3", …
    /// Never overwrites an existing file.</summary>
    private static string UniquePath(string folder, string baseName, string ext)
    {
        var path = Path.Combine(folder, baseName + ext);
        int n = 2;
        while (File.Exists(path) && n < 1000)
            path = Path.Combine(folder, $"{baseName} ({n++}){ext}");
        return path;
    }

    private static string Shorten(string s) => s.Length <= 60 ? s : s[..57] + "…";

    private static string? Str(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object
           && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
