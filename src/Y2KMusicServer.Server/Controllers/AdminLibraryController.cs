using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Data;
using Y2KMusicServer.Server.Updates;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// Admin library endpoints. Phase 1 surface: a stats probe proving the
/// database is alive end-to-end. Library search and mutation land in later
/// phases.
/// </summary>
[ApiController]
[Route("api/admin/library")]
public sealed class AdminLibraryController : ControllerBase
{
    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly IConfiguration _cfg;

    public AdminLibraryController(IDbContextFactory<Y2KDbContext> dbf, IConfiguration cfg)
    {
        _dbf = dbf;
        _cfg = cfg;
    }

    // The library browser loads the whole library in one request and scrolls it,
    // so this cap is only a guard against a pathological take= value.
    private const int MaxTake = 100_000;

    [HttpGet("stats")]
    public async Task<object> Stats(CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);

        var trackCount = await db.Tracks.CountAsync(ct);

        return new
        {
            trackCount,
            dbPath = DataPaths.DbPath(_cfg),
            version = GitHubUpdateChecker.CurrentVersion()
        };
    }

    /// <summary>
    /// Track listing for the admin library browser. Optional free-text
    /// <paramref name="q"/> (title / artist / album, case-insensitive LIKE) plus
    /// the three facet filters: <paramref name="format"/> ("FLAC" / "MP3" /
    /// "Other"), <paramref name="genre"/> (a genre-map bucket, or "Unknown"),
    /// and <paramref name="decade"/> (a decade start year, e.g. 1980; 0 =
    /// unknown decade). Facets resolve via the genre map at query time, so the
    /// facet filtering happens in memory after the SQL text filter — fine at
    /// this library's scale. Each track row carries its effective
    /// <c>genreBucket</c> and <c>decade</c> for the grid.
    /// </summary>
    [HttpGet("tracks")]
    public async Task<object> Tracks(
        [FromQuery] string? q,
        [FromQuery] string? format,
        [FromQuery] string? genre,
        [FromQuery] int? decade,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, MaxTake);
        skip = Math.Max(0, skip);

        await using var db = await _dbf.CreateDbContextAsync(ct);
        var query = db.Tracks.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(format))
        {
            var f = format.Trim();
            query = string.Equals(f, "Other", StringComparison.OrdinalIgnoreCase)
                ? query.Where(t => t.Type != "FLAC" && t.Type != "MP3")
                : query.Where(t => t.Type == f.ToUpper());
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(t =>
                (t.Title != null && EF.Functions.Like(t.Title, $"%{term}%")) ||
                (t.Artist != null && EF.Functions.Like(t.Artist, $"%{term}%")) ||
                (t.Album != null && EF.Functions.Like(t.Album, $"%{term}%")));
        }

        var rows = await query
            .OrderBy(t => t.Artist).ThenBy(t => t.Title)
            .ToListAsync(ct);

        var map = GenreMapStore.Load(_cfg);
        var faceted = rows
            .Select(t => new
            {
                Track = t,
                GenreBucket = GenreMapStore.EffectiveGenre(map, t),
                Decade = GenreMapStore.Decade(t.Year)
            });

        if (!string.IsNullOrWhiteSpace(genre))
            faceted = faceted.Where(x =>
                string.Equals(x.GenreBucket, genre.Trim(), StringComparison.OrdinalIgnoreCase));

        if (decade is int d)
            faceted = faceted.Where(x => d == 0 ? x.Decade == null : x.Decade == d);

        var list = faceted.ToList();
        var total = list.Count;
        var items = list
            .Skip(skip).Take(take)
            .Select(x => new
            {
                x.Track.Id,
                x.Track.Title,
                x.Track.Artist,
                x.Track.Album,
                x.Track.DurationSec,
                x.Track.Bpm,
                lufs = x.Track.LufsIntegrated,
                x.Track.Type,
                genreBucket = x.GenreBucket,
                decade = x.Decade
            })
            .ToList();

        return new { total, skip, take, items };
    }

    /// <summary>Distinct raw tag genres with counts and where each currently
    /// maps — the genre-map editor's worklist (unmapped raws resolve to
    /// Unknown until a rule or bucket covers them).</summary>
    [HttpGet("raw-genres")]
    public async Task<object> RawGenres(CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var raws = await db.Tracks.AsNoTracking()
            .GroupBy(t => t.Genre ?? "")
            .Select(g => new { raw = g.Key, count = g.Count() })
            .ToListAsync(ct);

        var map = GenreMapStore.Load(_cfg);
        var items = raws
            .Where(r => !string.IsNullOrWhiteSpace(r.raw))
            .Select(r => new { r.raw, r.count, bucket = GenreMapStore.Resolve(map, r.raw) })
            .OrderByDescending(r => r.count)
            .ToList();
        int untagged = raws.Where(r => string.IsNullOrWhiteSpace(r.raw)).Sum(r => r.count);
        return new { items, untagged };
    }

    /// <summary>The facet values for the filter bar: formats, genre buckets
    /// (map order + Unknown), and the decades present in the library — each
    /// with a live track count under the current map.</summary>
    [HttpGet("facets")]
    public async Task<object> Facets(CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var rows = await db.Tracks.AsNoTracking()
            .Select(t => new { t.Type, t.Genre, t.GenreOverride, t.Year })
            .ToListAsync(ct);

        var map = GenreMapStore.Load(_cfg);

        var formats = rows
            .GroupBy(r => r.Type == "FLAC" || r.Type == "MP3" ? r.Type! : "Other")
            .Select(g => new { name = g.Key, count = g.Count() })
            .OrderBy(f => f.name)
            .ToList();

        var byBucket = rows
            .GroupBy(r => !string.IsNullOrWhiteSpace(r.GenreOverride)
                ? GenreMapStore.Resolve(map, r.GenreOverride)
                : GenreMapStore.Resolve(map, r.Genre))
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var genres = map.Buckets
            .Select(b => new { name = b, count = byBucket.TryGetValue(b, out var n) ? n : 0 })
            .Append(new
            {
                name = GenreMapStore.Unknown,
                count = byBucket.TryGetValue(GenreMapStore.Unknown, out var u) ? u : 0
            })
            .ToList();

        var decades = rows
            .GroupBy(r => GenreMapStore.Decade(r.Year))
            .Select(g => new { decade = g.Key ?? 0, count = g.Count() })
            .OrderBy(d => d.decade == 0 ? int.MaxValue : d.decade)
            .ToList();

        return new { formats, genres, decades };
    }
}
