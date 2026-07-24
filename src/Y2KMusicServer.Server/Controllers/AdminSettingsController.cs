using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Data;
using Y2KMusicServer.Server.Data.Entities;
using Y2KMusicServer.Server.Diagnostics;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// Reads / writes the user-mutable <see cref="Settings"/> row. PUT applies only
/// the fields present in the body (omitted = unchanged) and clamps them.
///
/// Auto DJ (<c>/api/admin/autodj/settings</c>) and streaming
/// (<c>/api/admin/stream/*</c>) keep their own endpoints: the streaming encoder
/// caches its enabled/bitrate state in memory, so writing those columns here
/// would desync the running stream. They're therefore returned by GET (for a
/// full view) but never written by PUT.
/// </summary>
[ApiController]
[Route("api/admin/settings")]
public sealed class AdminSettingsController : ControllerBase
{
    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly LogVerbositySwitch _verbosity;
    private readonly IConfiguration _cfg;

    public AdminSettingsController(IDbContextFactory<Y2KDbContext> dbf, LogVerbositySwitch verbosity, IConfiguration cfg)
    {
        _dbf = dbf;
        _verbosity = verbosity;
        _cfg = cfg;
    }

    /// <summary>General-settings patch. Streaming and Auto DJ are intentionally absent.</summary>
    public sealed record SettingsUpdate(
        bool? SmartMix, bool? SmartBeatFader, int? NextTriggerPct, int? NextFadeSeconds,
        bool? NormalizeEnabled, bool? LimiterEnabled, double? TargetLufs, int? Volume,
        int? ScanWorkers, bool? AllowWebNext, bool? ShowWebCategories, bool? DebugLogging,
        bool? ShowListenLive, bool? RequestLimitEnabled, int? RequestIntervalMinutes,
        bool? AutoAcceptRequests);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var s = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        return s == null ? Conflict(new { error = "settings row missing" }) : Ok(Shape(s, WebConfigStore.Load(_cfg)));
    }

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] SettingsUpdate? u, CancellationToken ct)
    {
        if (u == null) return BadRequest(new { error = "settings body required" });

        await using var db = await _dbf.CreateDbContextAsync(ct);
        var s = await db.Settings.FirstOrDefaultAsync(ct);
        if (s == null) return Conflict(new { error = "settings row missing" });

        if (u.SmartMix is bool sm) s.SmartMix = sm;
        if (u.SmartBeatFader is bool sb) s.SmartBeatFader = sb;
        if (u.NextTriggerPct is int nt) s.NextTriggerPct = Math.Clamp(nt, 5, 95);
        if (u.NextFadeSeconds is int nf) s.NextFadeSeconds = Math.Clamp(nf, 0, 30);
        if (u.NormalizeEnabled is bool ne) s.NormalizeEnabled = ne;
        if (u.LimiterEnabled is bool le) s.LimiterEnabled = le;
        if (u.TargetLufs is double tl) s.TargetLufs = Math.Clamp(tl, -40, 0);
        if (u.Volume is int v) s.Volume = Math.Clamp(v, 0, 100);
        if (u.ScanWorkers is int sw) s.ScanWorkers = Math.Clamp(sw, 1, 16);
        if (u.AllowWebNext is bool aw) s.AllowWebNext = aw;
        if (u.ShowWebCategories is bool sc) s.ShowWebCategories = sc;
        if (u.DebugLogging is bool dl) s.DebugLogging = dl;

        await db.SaveChangesAsync(ct);

        // Flip the live logging verbosity immediately so the change takes effect
        // without a service restart.
        if (u.DebugLogging is bool dlog) _verbosity.SetDebug(dlog);

        // The listener-facing flags live in web-config.json (no-migrations rule),
        // not the Settings row — apply only the ones present and persist.
        var web = WebConfigStore.Load(_cfg);
        if (u.ShowListenLive is bool sll) web.ShowListenLive = sll;
        if (u.RequestLimitEnabled is bool rle) web.RequestLimitEnabled = rle;
        if (u.RequestIntervalMinutes is int rim) web.RequestIntervalMinutes = rim;
        if (u.AutoAcceptRequests is bool aar) web.AutoAcceptRequests = aar;
        web = WebConfigStore.Save(_cfg, web);

        return Ok(Shape(s, web));
    }

    private static object Shape(Settings s, WebConfigStore.WebConfig w) => new
    {
        s.SmartMix, s.SmartBeatFader, s.NextTriggerPct, s.NextFadeSeconds,
        s.AutoDj, s.AutoDjTracks, s.AutoDjBpmDev, s.ScanWorkers,
        s.NormalizeEnabled, s.LimiterEnabled, s.TargetLufs, s.Volume,
        s.StreamingBitrate, s.AllowWebNext, s.ShowWebCategories,
        s.DebugLogging,
        w.ShowListenLive, w.RequestLimitEnabled, w.RequestIntervalMinutes, w.AutoAcceptRequests
    };
}
