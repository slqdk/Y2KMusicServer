using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Data;
using Y2KMusicServer.Server.Data.Entities;

namespace Y2KMusicServer.Server.Integrations;

/// <summary>
/// Fills missing genres, albums, and years from the Deezer public API (no key,
/// no account). One background pass at a time, scanner-style. It selects
/// tracks missing a raw genre tag OR an album, matches them on Deezer, and
/// fills the blanks — in the DATABASE ONLY, never the file:
///
///   • Genre  — the matched album's genre string, written into the raw
///     <see cref="Track.Genre"/> so it flows through the normal genre map
///     (new raw strings appear in the map editor's worklist).
///   • Album  — the matched album's title; if Deezer has no match but the
///     file's parent folder looks like an album, the folder name is used.
///   • Year   — the matched album's release year, filled only when the track
///     has no year at all (existing years are never overwritten, even wrong
///     compilation years — that correction stays manual).
///
/// Matching quality: the file's parent-folder name (when it isn't a scan-folder
/// root) acts as an album hint — among Deezer's candidates, a hit whose album
/// matches the folder wins. Titles are scrubbed of tag junk ("(1997)",
/// "(feat. …)", "(Track 01 – …)"), and when the artist tag is empty an
/// "Artist - Title" pattern in the title is tried both ways. A result is
/// accepted only when Deezer's artist loosely matches ours; anything else is a
/// counted miss and the track stays as it was (re-runs retry only what is
/// still missing).
///
/// Existing non-empty fields are never overwritten. Politeness: one HTTP call
/// at a time, throttled to ~5/s, album genres cached per pass.
/// </summary>
public sealed class GenreLookupService
{
    public sealed class LookupStatus
    {
        public bool Running { get; init; }
        public int Total { get; init; }
        public int Processed { get; init; }
        public int Found { get; init; }
        public int Misses { get; init; }
        public string? CurrentTrack { get; init; }
        public string? Message { get; init; }
    }

    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<GenreLookupService> _log;

    private readonly object _gate = new();
    private bool _busy;
    private volatile bool _cancel;
    private volatile LookupStatus _current = new() { Message = "Idle" };

    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(220);

    public GenreLookupService(IDbContextFactory<Y2KDbContext> dbf, IHttpClientFactory http,
        IConfiguration cfg, ILogger<GenreLookupService> log)
    {
        _dbf = dbf;
        _http = http;
        _cfg = cfg;
        _log = log;
    }

    public LookupStatus Current => _current;

    /// <summary>Starts a pass; false when one is already running.</summary>
    public bool TryStart()
    {
        lock (_gate)
        {
            if (_busy) return false;
            _busy = true;
            _cancel = false;
        }
        _ = Task.Run(RunAsync);
        return true;
    }

    /// <summary>Asks a running pass to stop after the current track.</summary>
    public void Stop() => _cancel = true;

    private async Task RunAsync()
    {
        try
        {
            await RunCoreAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Genre lookup failed");
            _current = new LookupStatus { Message = $"Failed: {ex.Message}" };
        }
        finally
        {
            lock (_gate) _busy = false;
        }
    }

    private sealed record WorkItem(int Id, string? Artist, string? Title, string? Album, int? Year, string FilePath);

    private async Task RunCoreAsync()
    {
        // Anything missing a raw genre or an album (and not manually pinned to
        // a bucket, in the genre case). Tracks complete on both are untouched.
        List<WorkItem> work;
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            work = (await db.Tracks.AsNoTracking()
                    .Where(t =>
                        ((t.Genre == null || t.Genre == "") && (t.GenreOverride == null || t.GenreOverride == "")) ||
                        t.Album == null || t.Album == "")
                    .OrderBy(t => t.Artist).ThenBy(t => t.Title)
                    .Select(t => new WorkItem(t.Id, t.Artist, t.Title, t.Album, t.Year, t.FilePath))
                    .ToListAsync());
        }

        if (work.Count == 0)
        {
            _current = new LookupStatus { Message = "Nothing to look up — genres and albums are filled." };
            return;
        }

        // Scan-folder roots: a parent folder equal to one of these is a library
        // root, not an album, so it never becomes an album hint.
        var roots = ScanFolderStore.AllPaths(_cfg)
            .Select(p => p.TrimEnd('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Y2KMusicServer/1.0");

        var albumGenreCache = new Dictionary<long, (string? Genre, int? Year)>();
        int processed = 0, found = 0, misses = 0, genres = 0, albums = 0, years = 0;
        _log.LogInformation("Genre/album lookup started: {Count} track(s) with blanks.", work.Count);

        foreach (var item in work)
        {
            if (_cancel)
            {
                _current = new LookupStatus
                {
                    Total = work.Count, Processed = processed, Found = found, Misses = misses,
                    Message = $"Stopped: {genres} genres, {albums} albums, {years} years filled."
                };
                _log.LogInformation("Genre lookup stopped: {G} genres, {A} albums, {Y} years, {M} misses, {Left} left.",
                    genres, albums, years, misses, work.Count - processed);
                return;
            }

            _current = new LookupStatus
            {
                Running = true, Total = work.Count, Processed = processed,
                Found = found, Misses = misses,
                CurrentTrack = $"{item.Artist} — {item.Title}"
            };

            var albumHint = AlbumHintFromPath(item.FilePath, roots);

            Match? match = null;
            try
            {
                match = await LookupOneAsync(client, albumGenreCache, item, albumHint);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Lookup error for {Artist} — {Title}", item.Artist, item.Title);
            }

            // Album can still come from the folder name when Deezer whiffs.
            string? fillAlbum = match?.Album ?? albumHint;

            if (match != null || (fillAlbum != null && string.IsNullOrWhiteSpace(item.Album)))
            {
                await using var db = await _dbf.CreateDbContextAsync();
                var t = await db.Tracks.FirstOrDefaultAsync(x => x.Id == item.Id);
                if (t != null)
                {
                    bool any = false;
                    if (match?.Genre is string g && string.IsNullOrWhiteSpace(t.Genre))
                    { t.Genre = g.Trim(); genres++; any = true; }
                    if (fillAlbum is string al && string.IsNullOrWhiteSpace(t.Album))
                    { t.Album = al.Trim(); albums++; any = true; }
                    if (match?.Year is int y && t.Year is null)
                    { t.Year = y; years++; any = true; }
                    if (any) { await db.SaveChangesAsync(); found++; } else misses++;
                }
            }
            else
            {
                misses++;
            }

            processed++;
            await Task.Delay(Throttle);
        }

        _current = new LookupStatus
        {
            Total = work.Count, Processed = processed, Found = found, Misses = misses,
            Message = $"Done: {genres} genres, {albums} albums, {years} years filled; {misses} without a match."
        };
        _log.LogInformation("Genre lookup completed: {G} genres, {A} albums, {Y} years filled, {M} misses of {Count}.",
            genres, albums, years, misses, work.Count);
    }

    private sealed record Match(string? Genre, string? Album, int? Year);
    private sealed record Candidate(string Artist, string Album, long AlbumId);

    /// <summary>One track: search (strict → scrubbed → plain, with artist
    /// recovery from the title), score candidates against the artist and the
    /// folder album hint, then fetch the winning album's genre + year.</summary>
    private async Task<Match?> LookupOneAsync(HttpClient client,
        Dictionary<long, (string? Genre, int? Year)> albumCache, WorkItem item, string? albumHint)
    {
        var (artist, title) = RecoverArtistTitle(item.Artist, item.Title);
        if (string.IsNullOrWhiteSpace(title)) return null;

        var clean = CleanTitle(title);
        if (clean.Length == 0) return null;

        // Query ladder: strict field search, then a fully de-parenthesised
        // title, then plain text. Stop at the first ladder rung with candidates.
        var queries = new List<string>();
        if (!string.IsNullOrWhiteSpace(artist))
            queries.Add($"artist:\"{artist.Trim()}\" track:\"{clean}\"");
        var bare = StripAllParens(clean);
        if (!string.IsNullOrWhiteSpace(artist) && bare.Length > 0 && bare != clean)
            queries.Add($"artist:\"{artist.Trim()}\" track:\"{bare}\"");
        queries.Add($"{artist} {(bare.Length > 0 ? bare : clean)}".Trim());

        List<Candidate> candidates = new();
        foreach (var q in queries)
        {
            candidates = await SearchAsync(client, q);
            if (candidates.Count > 0) break;
            await Task.Delay(Throttle);
        }
        if (candidates.Count == 0) return null;

        // Artist agreement (loose: one folds-contains the other). With no
        // artist of our own, accept — the title match is all we have.
        var ours = Fold(artist ?? "");
        var agreeing = candidates.Where(c =>
        {
            if (ours.Length == 0) return true;
            var theirs = Fold(c.Artist);
            return theirs.Length > 0 && (ours.Contains(theirs) || theirs.Contains(ours));
        }).ToList();
        if (agreeing.Count == 0) return null;

        // Album-hint preference: a candidate whose album matches the folder
        // name is very likely the right pressing (and the right genre/year).
        Candidate pick = agreeing[0];
        if (albumHint != null)
        {
            var hintFold = Fold(albumHint);
            var byHint = agreeing.FirstOrDefault(c =>
            {
                var af = Fold(c.Album);
                return af.Length > 0 && hintFold.Length > 0 &&
                       (af.Contains(hintFold) || hintFold.Contains(af));
            });
            if (byHint != null) pick = byHint;
        }

        if (!albumCache.TryGetValue(pick.AlbumId, out var albumInfo))
        {
            await Task.Delay(Throttle);
            albumInfo = await AlbumInfoAsync(client, pick.AlbumId);
            albumCache[pick.AlbumId] = albumInfo;
        }

        return new Match(albumInfo.Genre, pick.Album, albumInfo.Year);
    }

    private static async Task<List<Candidate>> SearchAsync(HttpClient client, string query)
    {
        var url = "https://api.deezer.com/search?limit=5&q=" + Uri.EscapeDataString(query);
        using var resp = await client.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return new();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return new();

        var list = new List<Candidate>();
        foreach (var el in data.EnumerateArray())
        {
            var artist = el.TryGetProperty("artist", out var a) &&
                         a.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
            var album = el.TryGetProperty("album", out var al) &&
                        al.TryGetProperty("title", out var at) ? at.GetString() ?? "" : "";
            long albumId = el.TryGetProperty("album", out var al2) &&
                           al2.TryGetProperty("id", out var ai) ? ai.GetInt64() : 0;
            if (albumId > 0) list.Add(new Candidate(artist, album, albumId));
        }
        return list;
    }

    private static async Task<(string? Genre, int? Year)> AlbumInfoAsync(HttpClient client, long albumId)
    {
        using var resp = await client.GetAsync($"https://api.deezer.com/album/{albumId}");
        if (!resp.IsSuccessStatusCode) return (null, null);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        string? genre = null;
        if (doc.RootElement.TryGetProperty("genres", out var genres) &&
            genres.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 &&
            data[0].TryGetProperty("name", out var name))
            genre = name.GetString();

        int? year = null;
        if (doc.RootElement.TryGetProperty("release_date", out var rd) &&
            rd.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(rd.GetString(), out var date) && date.Year > 1900)
            year = date.Year;

        return (genre, year);
    }

    /// <summary>The file's parent folder as an album hint — null when it is a
    /// scan-folder root (library root, not an album).</summary>
    private static string? AlbumHintFromPath(string filePath, HashSet<string> roots)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath)?.TrimEnd('\\', '/');
            if (string.IsNullOrEmpty(dir) || roots.Contains(dir)) return null;
            var name = Path.GetFileName(dir);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch { return null; }
    }

    /// <summary>With an empty artist tag and a title like "Artist - Title"
    /// (common in loose rips), split on the first " - ".</summary>
    private static (string? Artist, string? Title) RecoverArtistTitle(string? artist, string? title)
    {
        if (!string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
            return (artist, title);
        var idx = title.IndexOf(" - ", StringComparison.Ordinal);
        if (idx <= 0 || idx >= title.Length - 3) return (artist, title);
        return (title[..idx].Trim(), title[(idx + 3)..].Trim());
    }

    /// <summary>Strips search-hostile suffixes: trailing "(1997)" years,
    /// "(feat. …)" credits, and "(Track 01 …)" rip markers.</summary>
    private static string CleanTitle(string title)
    {
        var t = Regex.Replace(title, @"\s*\((?:19|20)\d\d\)\s*$", "");
        t = Regex.Replace(t, @"\s*[\(\[]feat\.?[^\)\]]*[\)\]]", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*[\(\[]track\s*\d+[^\)\]]*[\)\]]", "", RegexOptions.IgnoreCase);
        return t.Trim();
    }

    /// <summary>Removes every remaining parenthetical — the last-ditch search
    /// form for titles like "Alone (Radio Edit) (Remastered)".</summary>
    private static string StripAllParens(string title)
        => Regex.Replace(title, @"\s*[\(\[][^\)\]]*[\)\]]", "").Trim();

    private static string Fold(string s) =>
        new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
