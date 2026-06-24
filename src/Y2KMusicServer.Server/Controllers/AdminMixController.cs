using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Audio;
using Y2KMusicServer.Server.Data;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// Auto-mix configuration and a dry-run planner. The rules persist to disk
/// (<c>mixrules.json</c>), not the schema. The plan endpoint reports which
/// transition the planner WOULD choose for a pair and executes nothing — the
/// crossfade path is untouched until the phase-4 executor lands. Strategy
/// behaviour lives in audio-engine.md.
/// </summary>
[ApiController]
[Route("api/admin/mix")]
public sealed class AdminMixController : ControllerBase
{
    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly IConfiguration _cfg;
    private readonly ILogger<AdminMixController> _log;

    public AdminMixController(IDbContextFactory<Y2KDbContext> dbf, IConfiguration cfg, ILogger<AdminMixController> log)
    {
        _dbf = dbf;
        _cfg = cfg;
        _log = log;
    }

    /// <summary>The current auto-mix rules (from mixrules.json, or defaults).</summary>
    [HttpGet("rules")]
    public IActionResult GetRules() => Ok(MixRules.Load(_cfg));

    /// <summary>Replace the auto-mix rules. Values are clamped to sane ranges;
    /// returns what was stored.</summary>
    [HttpPut("rules")]
    public IActionResult PutRules([FromBody] MixRules? rules)
    {
        if (rules is null) return BadRequest(new { error = "body required" });
        return Ok(MixRules.Save(_cfg, rules));
    }

    /// <summary>
    /// Dry-run: the transition the planner would pick for <c>from</c> → <c>to</c>,
    /// building the structure caches on a miss. Plans regardless of the master
    /// <c>Enabled</c> flag (so you can preview); per-strategy toggles still apply.
    /// Executes nothing. 404 if either track is unknown.
    /// </summary>
    [HttpGet("plan")]
    public async Task<IActionResult> GetPlan([FromQuery] int fromId, [FromQuery] int toId, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var a = await db.Tracks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fromId, ct);
        var b = await db.Tracks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == toId, ct);
        if (a is null) return NotFound(new { error = "from track not found", fromId });
        if (b is null) return NotFound(new { error = "to track not found", toId });

        // Structure caches (best-effort; the planner tolerates nulls and falls back).
        TrackStructureData? aStruct = null, bStruct = null;
        try { aStruct = await Task.Run(() => TrackStructure.GetOrBuild(_cfg, a.Id, a.FilePath), ct); } catch { }
        try { bStruct = await Task.Run(() => TrackStructure.GetOrBuild(_cfg, b.Id, b.FilePath), ct); } catch { }

        // Base mix points. smartMode → the fade length is BPM-derived and the
        // passed 6 s is ignored; it only influences the out-point ceiling.
        MixPoints basePoints;
        try
        {
            basePoints = await Task.Run(() => MixAnalyser.AnalysePair(
                a.FilePath, a.Bpm ?? 0, a.BeatPhaseOffsetSec ?? 0,
                b.FilePath, b.Bpm ?? 0, b.BeatPhaseOffsetSec ?? 0,
                6.0, ct, smartMode: true), ct);
        }
        catch
        {
            basePoints = new MixPoints();
        }

        var rules = MixRules.Load(_cfg);
        var plan = MixPlanner.Plan(basePoints, a.Bpm, b.Bpm, b.BeatPhaseOffsetSec, aStruct, bStruct, rules);

        _log.LogInformation("MixPlan {From}->{To}: {Strategy} | {Reason}",
            a.Id, b.Id, plan.StrategyName, plan.Reason);

        return Ok(new
        {
            from = new { a.Id, a.Title, a.Artist, bpm = a.Bpm },
            to = new { b.Id, b.Title, b.Artist, bpm = b.Bpm },
            masterEnabled = rules.Enabled,
            plan
        });
    }
}
