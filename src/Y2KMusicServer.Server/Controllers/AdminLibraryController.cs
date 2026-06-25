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
        var categoryCount = await db.Categories.CountAsync(ct);

        return new
        {
            trackCount,
            categoryCount,
            dbPath = DataPaths.DbPath(_cfg),
            version = GitHubUpdateChecker.CurrentVersion()
        };
    }

    /// <summary>
    /// Track listing for the admin library browser. Optional free-text
    /// <paramref name="q"/> (title / artist / album, case-insensitive LIKE) and
    /// <paramref name="categoryId"/> filter. <paramref name="take"/> is clamped
    /// to 1..<see cref="MaxTake"/>; the browser requests the whole library in one
    /// call and scrolls it.
    /// </summary>
    [HttpGet("tracks")]
    public async Task<object> Tracks(
        [FromQuery] string? q,
        [FromQuery] int? categoryId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, MaxTake);
        skip = Math.Max(0, skip);

        await using var db = await _dbf.CreateDbContextAsync(ct);
        var query = db.Tracks.AsNoTracking().AsQueryable();

        if (categoryId is int cid)
            query = query.Where(t => t.CategoryId == cid);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(t =>
                (t.Title != null && EF.Functions.Like(t.Title, $"%{term}%")) ||
                (t.Artist != null && EF.Functions.Like(t.Artist, $"%{term}%")) ||
                (t.Album != null && EF.Functions.Like(t.Album, $"%{term}%")));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(t => t.Artist).ThenBy(t => t.Title)
            .Skip(skip).Take(take)
            .Select(t => new
            {
                t.Id,
                t.Title,
                t.Artist,
                t.Album,
                t.DurationSec,
                t.Bpm,
                lufs = t.LufsIntegrated,
                t.Type,
                t.CategoryId
            })
            .ToListAsync(ct);

        return new { total, skip, take, items };
    }
}
