using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Data;
using Y2KMusicServer.Server.Data.Entities;

namespace Y2KMusicServer.Server.Integrations;

/// <summary>
/// Fills missing genres from the Deezer public API (no key, no account). One
/// background pass at a time, scanner-style: it selects tracks whose effective
/// genre is Unknown AND whose raw tag genre is empty, searches Deezer by
/// artist + title, and writes the matched album's genre string into the
/// track's raw <see cref="Track.Genre"/> — in the DATABASE ONLY, never the
/// file. The result then flows through the normal genre map: new raw strings
/// ("Eurodance", "Rap/Hip Hop", …) appear in the map editor's worklist and one
/// rule covers every track that received them.
///
/// Tracks that already carry an (unmapped) raw tag genre are deliberately NOT
/// looked up or overwritten — they resolve to Unknown only until a map rule
/// covers them, and the tag is real data worth keeping. The map editor is the
/// tool for those.
///
/// Politeness: one HTTP call at a time, throttled to ~5/s, with album genres
/// cached per pass so a 12-track album costs one album fetch. A result is
/// accepted only when Deezer's artist loosely matches ours; anything else is a
/// counted miss and the track stays Unknown (re-runs retry only what is still
/// missing).
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
    private readonly ILogger<GenreLookupService> _log;

    private readonly object _gate = new();
    private bool _busy;
    private volatile bool _cancel;
    private volatile LookupStatus _current = new() { Message = "Idle" };

    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(220);

    public GenreLookupService(IDbContextFactory<Y2KDbContext> dbf, IHttpClientFactory http,
        ILogger<GenreLookupService> log)
    {
        _dbf = dbf;
        _http = http;
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

    private async Task RunCoreAsync()
    {
        // Only tracks that are Unknown for lack of any tag at all. A non-empty
        // raw genre (even an unmapped one) is handled by the map editor, not here.
        List<(int Id, string? Artist, string? Title)> work;
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            work = (await db.Tracks.AsNoTracking()
                    .Where(t => (t.Genre == null || t.Genre == "") &&
                                (t.GenreOverride == null || t.GenreOverride == ""))
                    .OrderBy(t => t.Artist).ThenBy(t => t.Title)
                    .Select(t => new { t.Id, t.Artist, t.Title })
                    .ToListAsync())
                .Select(x => (x.Id, x.Artist, x.Title))
                .ToList();
        }

        if (work.Count == 0)
        {
            _current = new LookupStatus { Message = "Nothing to look up — no untagged tracks." };
            return;
        }

        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Y2KMusicServer/1.0");

        var albumGenreCache = new Dictionary<long, string?>();
        int processed = 0, found = 0, misses = 0;
        _log.LogInformation("Genre lookup started: {Count} untagged track(s).", work.Count);

        foreach (var (id, artist, title) in work)
        {
            if (_cancel)
            {
                _current = new LookupStatus
                {
                    Total = work.Count, Processed = processed, Found = found, Misses = misses,
                    Message = "Stopped."
                };
                _log.LogInformation("Genre lookup stopped: {Found} found, {Misses} misses, {Left} left.",
                    found, misses, work.Count - processed);
                return;
            }

            _current = new LookupStatus
            {
                Running = true, Total = work.Count, Processed = processed,
                Found = found, Misses = misses,
                CurrentTrack = $"{artist} — {title}"
            };

            string? genre = null;
            try
            {
                genre = await LookupOneAsync(client, albumGenreCache, artist, title);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Genre lookup error for {Artist} — {Title}", artist, title);
            }

            if (!string.IsNullOrWhiteSpace(genre))
            {
                await using var db = await _dbf.CreateDbContextAsync();
                var t = await db.Tracks.FirstOrDefaultAsync(x => x.Id == id);
                // Fill only if still untagged (a rescan or edit may have raced us).
                if (t != null && string.IsNullOrWhiteSpace(t.Genre))
                {
                    t.Genre = genre.Trim();
                    await db.SaveChangesAsync();
                    found++;
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
            Message = $"Done: {found} genres found, {misses} without a confident match."
        };
        _log.LogInformation("Genre lookup completed: {Found} found, {Misses} misses of {Count}.",
            found, misses, work.Count);
    }

    /// <summary>One track: Deezer search → artist sanity check → album genre.</summary>
    private async Task<string?> LookupOneAsync(HttpClient client, Dictionary<long, string?> albumCache,
        string? artist, string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var cleanTitle = CleanTitle(title);
        if (cleanTitle.Length == 0) return null;

        // artist:"" track:"" narrows the match hard; a plain-text query is the
        // fallback when the strict form finds nothing (compilations, features).
        string strict = string.IsNullOrWhiteSpace(artist)
            ? $"track:\"{cleanTitle}\""
            : $"artist:\"{artist!.Trim()}\" track:\"{cleanTitle}\"";

        var hit = await SearchAsync(client, strict) ?? await SearchAsync(client, $"{artist} {cleanTitle}".Trim());
        if (hit == null) return null;

        // Loose artist agreement: one contains the other (case-insensitive).
        // Skip the check when we have no artist of our own.
        if (!string.IsNullOrWhiteSpace(artist))
        {
            var ours = Fold(artist!);
            var theirs = Fold(hit.Value.Artist);
            if (ours.Length > 0 && theirs.Length > 0 &&
                !ours.Contains(theirs) && !theirs.Contains(ours))
                return null;
        }

        // Album genre, cached per pass.
        if (albumCache.TryGetValue(hit.Value.AlbumId, out var cached)) return cached;
        await Task.Delay(Throttle);
        var genre = await AlbumGenreAsync(client, hit.Value.AlbumId);
        albumCache[hit.Value.AlbumId] = genre;
        return genre;
    }

    private static async Task<(string Artist, long AlbumId)?> SearchAsync(HttpClient client, string query)
    {
        var url = "https://api.deezer.com/search?limit=1&q=" + Uri.EscapeDataString(query);
        using var resp = await client.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            return null;

        var first = data[0];
        var artist = first.TryGetProperty("artist", out var a) &&
                     a.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
        long albumId = first.TryGetProperty("album", out var al) &&
                       al.TryGetProperty("id", out var ai) ? ai.GetInt64() : 0;
        return albumId > 0 ? (artist, albumId) : null;
    }

    private static async Task<string?> AlbumGenreAsync(HttpClient client, long albumId)
    {
        using var resp = await client.GetAsync($"https://api.deezer.com/album/{albumId}");
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("genres", out var genres) &&
            genres.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 &&
            data[0].TryGetProperty("name", out var name))
            return name.GetString();
        return null;
    }

    /// <summary>Strips search-hostile suffixes the library's filename-derived
    /// titles carry: trailing "(1997)" years and "(feat. …)" credits.</summary>
    private static string CleanTitle(string title)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(title, @"\s*\((?:19|20)\d\d\)\s*$", "");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s*[\(\[]feat\.?[^\)\]]*[\)\]]", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return t.Trim();
    }

    private static string Fold(string s) =>
        new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
