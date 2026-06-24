using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Audio;
using Y2KMusicServer.Server.Data;
using Y2KMusicServer.Server.Data.Entities;
using Y2KMusicServer.Server.Playback;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// Admin side of listener requests: list them and accept (adds to the playlist
/// as a <see cref="PlaylistSource.Request"/> entry, inserted before the next
/// Auto track) or dismiss.
/// </summary>
[ApiController]
[Route("api/admin/requests")]
public sealed class AdminRequestsController : ControllerBase
{
    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly PlaylistService _playlist;
    private readonly AudioEngine _engine;

    public AdminRequestsController(IDbContextFactory<Y2KDbContext> dbf, PlaylistService playlist, AudioEngine engine)
    {
        _dbf = dbf;
        _playlist = playlist;
        _engine = engine;
    }

    /// <summary>Pending first, then recently actioned (capped).</summary>
    [HttpGet]
    public async Task<object> List(CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        return await db.Requests.AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(50)
            .Select(r => new
            {
                r.Id,
                r.TrackId,
                title = r.Track!.Title,
                artist = r.Track!.Artist,
                r.RequesterName,
                status = r.Status.ToString(),
                r.CreatedAt
            })
            .ToListAsync(ct);
    }

    [HttpPost("{id:int}/accept")]
    public async Task<IActionResult> Accept(int id, CancellationToken ct)
    {
        int trackId; string? who;
        await using (var db = await _dbf.CreateDbContextAsync(ct))
        {
            var r = await db.Requests.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (r == null) return NotFound();
            r.Status = RequestStatus.Accepted;
            trackId = r.TrackId;
            who = r.RequesterName;
            await db.SaveChangesAsync(ct);
        }

        int? current = _engine.GetStatus().TrackId;
        await _playlist.AddAsync(trackId, PlaylistSource.Request, who ?? "Request", current, ct);
        return Ok(new { ok = true });
    }

    [HttpPost("{id:int}/dismiss")]
    public async Task<IActionResult> Dismiss(int id, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var r = await db.Requests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r == null) return NotFound();
        r.Status = RequestStatus.Dismissed;
        await db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }
}
