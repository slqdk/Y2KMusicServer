using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Audio;
using Y2KMusicServer.Server.Data;
using Y2KMusicServer.Server.Data.Entities;

namespace Y2KMusicServer.Server.Integrations;

/// <summary>A YouTube Music search hit — metadata only, from a flat search
/// (no media resolved yet). <see cref="DurationSec"/> is 0 when the flat result
/// didn't carry a duration.</summary>
public sealed record YouTubeSearchItem(
    string Id, string Title, string? Artist, double DurationSec, string Url);

/// <summary>Outcome of fetching (download + cache + index) one track. On success
/// <see cref="TrackId"/> is a normal library track id the caller can queue via
/// the existing playlist endpoints.</summary>
public sealed class YouTubeFetchResult
{
    public required bool Ok { get; init; }
    public int? TrackId { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public double DurationSec { get; init; }
    public bool AlreadyCached { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// The YouTube fetch path: search YouTube Music for candidates, then download a
/// chosen track's audio into the local web-cache and index it as an ordinary
/// <see cref="Track"/> row so it plays through the existing engine (crossfade,
/// loudness, /stream) with no special-casing. Search is metadata-only and fast;
/// fetch does the download + transcode-to-MP3 (so the file is guaranteed to
/// decode in the engine) and the row insert. Fetch is idempotent by video id —
/// a second fetch of the same track reuses the cached file and row.
///
/// Uses the same yt-dlp / ffmpeg binaries the preflight (<see cref="YouTubeProbe"/>)
/// verifies, pathed from appsettings (Integrations:YouTube:*). Failures are
/// returned as a result with a plain-English message, not thrown.
/// </summary>
public sealed class YouTubeFetchService
{
    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly IConfiguration _cfg;
    private readonly ILogger<YouTubeFetchService> _log;
    private readonly string _ytDlp;

    // YouTube video ids are 11 chars of [A-Za-z0-9_-]. Validated before use so a
    // stray value can't shape an odd cache filename or command argument.
    private static readonly Regex VideoId = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    // A video id embedded in any of the link shapes YouTube / YouTube Music hand
    // out (watch?v=, youtu.be/, /shorts/, /embed/, /live/). Used so a pasted link
    // resolves to that exact track instead of being run as a search string.
    private static readonly Regex LinkVideoId = new(
        @"(?:[?&]v=|youtu\.be/|/shorts/|/embed/|/live/)([A-Za-z0-9_-]{11})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public YouTubeFetchService(IDbContextFactory<Y2KDbContext> dbf, IConfiguration cfg,
                               ILogger<YouTubeFetchService> log)
    {
        _dbf = dbf;
        _cfg = cfg;
        _log = log;
        _ytDlp = cfg["Integrations:YouTube:YtDlpPath"] ?? "yt-dlp";
    }

    // ── Search ────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a query to candidate tracks. The query can be:
    /// a pasted video link (watch / music.youtube / youtu.be / shorts), a bare
    /// 11-char video id, a pasted playlist or album link (its entries are
    /// listed), or plain search words.
    ///
    /// Search words are tried on YouTube Music first (<c>ytmsearch</c>) and fall
    /// back to plain YouTube (<c>ytsearch</c>) when Music returns nothing —
    /// the Music extractor is the more fragile of the two and answers a bare
    /// <c>null</c> document when it can't build a result page.
    ///
    /// Never throws on a bad / empty / non-object yt-dlp document: a failure is
    /// logged with yt-dlp's own first error line and returns an empty list.
    /// </summary>
    public async Task<IReadOnlyList<YouTubeSearchItem>> SearchAsync(
        string query, int limit, CancellationToken ct)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0) return Array.Empty<YouTubeSearchItem>();
        limit = Math.Clamp(limit, 1, 25);

        // Strip a surrounding pair of quotes/angle brackets — pasted links often
        // arrive wrapped by the source app.
        query = query.Trim('"', '\'', '<', '>', ' ');

        // 1. A pasted link or bare id → resolve that exact video. The canonical
        //    watch URL is rebuilt from the id, which drops any &list= so a track
        //    pasted from inside a playlist doesn't drag the playlist in.
        var m = LinkVideoId.Match(query);
        string? direct = m.Success ? m.Groups[1].Value
                       : VideoId.IsMatch(query) ? query
                       : null;
        if (direct != null)
            return await ResolveAsync($"https://www.youtube.com/watch?v={direct}", 1, true, ct);

        // 2. Any other pasted URL (playlist / album / channel) → list its entries.
        if (query.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || query.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return await ResolveAsync(query, limit, false, ct);

        // 3. Plain words → Music search, then plain YouTube search.
        var items = await ResolveAsync($"ytmsearch{limit}:{query}", limit, false, ct);
        if (items.Count > 0) return items;
        return await ResolveAsync($"ytsearch{limit}:{query}", limit, false, ct);
    }

    /// <summary>Runs one flat yt-dlp extraction and collects up to
    /// <paramref name="limit"/> video entries out of whatever shape it returns.</summary>
    private async Task<IReadOnlyList<YouTubeSearchItem>> ResolveAsync(
        string target, int limit, bool noPlaylist, CancellationToken ct)
    {
        // Flat: fast, metadata only, no per-result media extraction.
        var args = new List<string> { "--flat-playlist", "-J", "--no-warnings" };
        if (noPlaylist) args.Add("--no-playlist");   // a watch link inside a playlist
        args.Add(target);

        var r = await RunAsync(_ytDlp, args.ToArray(), TimeSpan.FromSeconds(45), ct);

        if (r.LaunchError != null || r.TimedOut || r.Stdout.Trim().Length == 0)
        {
            _log.LogWarning("YouTube search failed for {Target}: {Err}", target,
                r.LaunchError ?? (r.TimedOut ? "timed out (45s)" : FirstMeaningful(r.Stderr)));
            return Array.Empty<YouTubeSearchItem>();
        }

        var items = new List<YouTubeSearchItem>();
        try
        {
            using var doc = JsonDocument.Parse(r.Stdout);

            // yt-dlp prints a bare `null` document when an extractor returns no
            // info dict (a Music search that couldn't build a result page does
            // exactly this). Guard the kind before touching properties — reading
            // one off a null/array root throws InvalidOperationException.
            if (doc.RootElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                _log.LogWarning("YouTube search for {Target} returned an empty result: {Err}",
                    target, FirstMeaningful(r.Stderr));
                return Array.Empty<YouTubeSearchItem>();
            }

            Collect(doc.RootElement, items, limit, 0);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "YouTube search for {Target} returned unparseable JSON", target);
        }
        return items;
    }

    /// <summary>Walks a yt-dlp info document and appends every video entry it
    /// finds. Handles all three shapes: a single video object, a playlist with an
    /// <c>entries</c> array, and a Music search page whose entries are themselves
    /// sectioned playlists (Songs / Videos / Albums).</summary>
    private static void Collect(JsonElement el, List<YouTubeSearchItem> items, int limit, int depth)
    {
        if (items.Count >= limit || depth > 4) return;

        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in el.EnumerateArray())
            {
                Collect(child, items, limit, depth + 1);
                if (items.Count >= limit) return;
            }
            return;
        }

        if (el.ValueKind != JsonValueKind.Object) return;

        bool isContainer = el.TryGetProperty("entries", out var entries)
                           && entries.ValueKind == JsonValueKind.Array;
        if (isContainer)
        {
            foreach (var child in entries.EnumerateArray())
            {
                Collect(child, items, limit, depth + 1);
                if (items.Count >= limit) return;
            }
            return;
        }

        if (Str(el, "_type") == "playlist") return;

        string? id = Str(el, "id");
        if (id is null || !VideoId.IsMatch(id)) return;
        if (items.Any(i => i.Id == id)) return;

        string title = Str(el, "title") ?? id;
        string? artist = Str(el, "artist") ?? Str(el, "creator")
                      ?? Str(el, "uploader") ?? Str(el, "channel");
        double dur = Num(el, "duration");
        items.Add(new YouTubeSearchItem(id, title, artist, dur,
            $"https://www.youtube.com/watch?v={id}"));
    }

    // ── Fetch (download + cache + index) ───────────────────────────────────

    public async Task<YouTubeFetchResult> FetchAsync(string videoId, CancellationToken ct)
    {
        if (videoId is null || !VideoId.IsMatch(videoId))
            return Fail("Invalid YouTube video id.");

        var dir = DataPaths.EnsureWebCacheDir(_cfg);
        var path = Path.Combine(dir, videoId + ".mp3");

        // Reuse an already-cached + indexed track (idempotent by video id).
        await using (var db = await _dbf.CreateDbContextAsync(ct))
        {
            var existing = await db.Tracks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.FilePath == path, ct);
            if (existing != null && File.Exists(path))
                return Done(existing.Id, existing.Title, existing.Artist,
                            existing.DurationSec, alreadyCached: true);
        }

        // Download bestaudio → MP3 (guaranteed engine decode), tags embedded so
        // the same TagLib read the scanner uses fills Title/Artist/duration.
        // yt-dlp skips the download if the final file is already present.
        var r = await RunAsync(_ytDlp, new[]
        {
            "-x", "--audio-format", "mp3", "--audio-quality", "0",
            "--embed-metadata", "--no-playlist", "--no-warnings",
            "-o", Path.Combine(dir, "%(id)s.%(ext)s"),
            $"https://www.youtube.com/watch?v={videoId}"
        }, TimeSpan.FromSeconds(180), ct);

        if (!File.Exists(path))
        {
            var why = r.LaunchError ?? (r.TimedOut ? "Download timed out." : FirstMeaningful(r.Stderr));
            _log.LogWarning("YouTube fetch failed for {Id}: {Why}", videoId, why);
            return Fail(why.Length > 0 ? why : "Download failed (no output file).");
        }

        // Read tags from the cached file (mirrors the scanner's ReadTrack).
        string? title = null, artist = null, album = null;
        double duration = 0;
        try
        {
            using var tf = TagLib.File.Create(path);
            title = NullIfBlank(tf.Tag.Title);
            artist = NullIfBlank(tf.Tag.FirstPerformer) ?? NullIfBlank(tf.Tag.FirstAlbumArtist);
            album = NullIfBlank(tf.Tag.Album);
            duration = tf.Properties?.Duration.TotalSeconds ?? 0;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Tag read failed for cached {Path}; using minimal metadata", path);
        }

        // Index it: an ordinary Tracks row with NO category (so Auto DJ never
        // auto-picks it), pointing at the cache file. A concurrent insert (unique
        // FilePath) is handled by re-reading the winner.
        int trackId;
        string? savedTitle;
        string? savedArtist;
        double savedDuration;
        await using (var db = await _dbf.CreateDbContextAsync(ct))
        {
            var track = new Track
            {
                FilePath = path,
                Title = title ?? videoId,
                Artist = artist,
                Album = album,
                Type = "MP3",
                DurationSec = duration,
                ScannedAt = DateTime.UtcNow
            };
            db.Tracks.Add(track);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                var again = await db.Tracks.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.FilePath == path, ct);
                if (again != null)
                    return Done(again.Id, again.Title, again.Artist, again.DurationSec, alreadyCached: true);
                return Fail("Could not index the downloaded track.");
            }
            trackId = track.Id;
            savedTitle = track.Title;
            savedArtist = track.Artist;
            savedDuration = track.DurationSec;
        }

        // Analyse inline. LoudnessAnalyzer decodes the whole file through the SAME
        // reader the engine plays with, so a null result means the file is silent /
        // too short / not decodable — i.e. it wouldn't play. Treat that as "no good":
        // prune the track (and any playlist entry, defensively) and drop the cache
        // file so a dud can never sit in the queue. On success, persist loudness +
        // tempo so the track crossfades and level-matches like a scanned one.
        double? lufs = null;
        try { lufs = new LoudnessAnalyzer().AnalyzeFile(path); } catch { /* → null → pruned */ }
        if (lufs == null)
        {
            await PruneTrackAsync(trackId, path, ct);
            _log.LogWarning("YouTube fetch: \"{Title}\" ({Id}) is not decodable — pruned track {TrackId}",
                savedTitle, videoId, trackId);
            return Fail("Downloaded file could not be decoded — skipped.");
        }

        BpmResult? bpm = null;
        try { bpm = new BpmDetector().AnalyzeFile(path); } catch { /* tempo optional */ }

        await using (var db = await _dbf.CreateDbContextAsync(ct))
        {
            var t = await db.Tracks.FirstOrDefaultAsync(x => x.Id == trackId, ct);
            if (t != null)
            {
                t.LufsIntegrated = lufs;
                if (bpm != null)
                {
                    t.Bpm = bpm.Bpm;
                    t.BpmConfidence = bpm.Confidence;
                    t.BeatPhaseOffsetSec = bpm.BeatPhaseOffsetSec;
                }
                await db.SaveChangesAsync(ct);
            }
        }

        _log.LogInformation("YouTube fetch: cached + analysed \"{Title}\" ({Id}) as track {TrackId} (LUFS {Lufs:F1})",
            savedTitle, videoId, trackId, lufs.Value);
        return Done(trackId, savedTitle, savedArtist, savedDuration, alreadyCached: false);
    }

    // Remove a just-fetched track that turned out unplayable: its playlist entries
    // (defensive — the fetch gate means it isn't queued yet), any request / mix-cache
    // rows, then the track, then the cache file. Mirrors the folder-clear delete
    // order in AdminFoldersController.
    private async Task PruneTrackAsync(int trackId, string path, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbf.CreateDbContextAsync(ct);
            await db.PlaylistEntries.Where(p => p.TrackId == trackId).ExecuteDeleteAsync(ct);
            await db.Requests.Where(r => r.TrackId == trackId).ExecuteDeleteAsync(ct);
            await db.MixCache.Where(m => m.FromTrackId == trackId || m.ToTrackId == trackId).ExecuteDeleteAsync(ct);
            await db.Tracks.Where(t => t.Id == trackId).ExecuteDeleteAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to prune undecodable web track {TrackId}", trackId);
        }
        try { if (File.Exists(path)) File.Delete(path); } catch { /* file may be briefly locked */ }
    }

    // ── result + json helpers ──────────────────────────────────────────────

    private static YouTubeFetchResult Done(int id, string? title, string? artist, double dur, bool alreadyCached)
        => new() { Ok = true, TrackId = id, Title = title, Artist = artist, DurationSec = dur, AlreadyCached = alreadyCached };

    private static YouTubeFetchResult Fail(string error)
        => new() { Ok = false, Error = error };

    private static string? Str(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object
           && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static double Num(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object
           && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetDouble(out var d) ? d : 0;

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string FirstMeaningful(string s)
    {
        foreach (var raw in s.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0) return line;
        }
        return "";
    }

    // ── process runner (self-contained; mirrors YouTubeProbe's) ─────────────

    private sealed record ProcResult(
        int ExitCode, string Stdout, string Stderr, bool TimedOut, string? LaunchError);

    private static async Task<ProcResult> RunAsync(
        string exe, string[] args, TimeSpan timeout, CancellationToken ct)
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
            return new ProcResult(-1, "", "", false, $"Not found or not runnable ({exe}): {ex.Message}");
        }

        var outT = proc.StandardOutput.ReadToEndAsync();
        var errT = proc.StandardError.ReadToEndAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return new ProcResult(-1, await Safe(outT), await Safe(errT), true, null);
        }
        return new ProcResult(proc.ExitCode, await outT, await errT, false, null);
    }

    private static async Task<string> Safe(Task<string> t)
    {
        try { return await t; } catch { return ""; }
    }
}
