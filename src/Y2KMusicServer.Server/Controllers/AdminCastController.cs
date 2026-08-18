using Microsoft.AspNetCore.Mvc;
using Y2KMusicServer.Server.Cast;
using Y2KMusicServer.Server.Data;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// Operator control for Google Cast speakers: the feature switch, the speaker
/// allow-list, and start/stop of the live stream on a speaker. The listener
/// page gets its own (much narrower) endpoints; this one is the full control.
/// </summary>
[ApiController]
[Route("api/admin/cast")]
public sealed class AdminCastController : ControllerBase
{
    private readonly CastService _cast;
    private readonly IConfiguration _cfg;
    private readonly ILogger<AdminCastController> _log;

    public AdminCastController(CastService cast, IConfiguration cfg, ILogger<AdminCastController> log)
    {
        _cast = cast;
        _cfg = cfg;
        _log = log;
    }

    /// <summary>Settings + the remembered speakers. No network traffic.</summary>
    [HttpGet("status")]
    public object Status()
    {
        var cfg = CastConfigStore.Load(_cfg);
        return new
        {
            cfg.Enabled,
            cfg.ShowOnListener,
            cfg.Volume,
            streamUrl = _cast.StreamUrl(cfg),
            streamUrlOverride = cfg.StreamUrl,
            detectedIp = CastService.LocalIPv4(),
            devices = _cast.Known()
        };
    }

    /// <summary>Runs an mDNS scan and returns the merged speaker list.</summary>
    [HttpPost("discover")]
    public async Task<object> Discover()
    {
        var devices = await _cast.DiscoverAsync(force: true);
        return new { devices };
    }

    public sealed record ConfigBody(bool? Enabled, bool? ShowOnListener, string? StreamUrl, double? Volume);

    /// <summary>Updates the cast settings. Switching the feature off stops
    /// every running cast, so the house speakers can't be left playing.</summary>
    [HttpPost("config")]
    public async Task<object> SetConfig([FromBody] ConfigBody? body)
    {
        var wasEnabled = CastConfigStore.Load(_cfg).Enabled;
        var cfg = CastConfigStore.Update(_cfg, c =>
        {
            if (body?.Enabled is bool e) c.Enabled = e;
            if (body?.ShowOnListener is bool s) c.ShowOnListener = s;
            if (body?.StreamUrl is string u) c.StreamUrl = u;
            if (body?.Volume is double v) c.Volume = v;
        });

        int stopped = 0;
        if (wasEnabled && !cfg.Enabled) stopped = await _cast.StopAllAsync();

        return new
        {
            cfg.Enabled,
            cfg.ShowOnListener,
            cfg.Volume,
            streamUrl = _cast.StreamUrl(cfg),
            streamUrlOverride = cfg.StreamUrl,
            stopped
        };
    }

    /// <summary>Allows or forbids one speaker. Forbidding stops it if it's playing.</summary>
    [HttpPost("{id}/allowed")]
    public async Task<IActionResult> SetAllowed(string id, [FromQuery] bool value)
    {
        bool found = false;
        CastConfigStore.Update(_cfg, c =>
        {
            var s = c.Speakers.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (s != null) { s.Allowed = value; found = true; }
        });
        if (!found) return NotFound(new { error = "unknown speaker" });

        if (!value) await _cast.StopAsync(id);
        return Ok(new { id, allowed = value, devices = _cast.Known() });
    }

    /// <summary>
    /// Decides whether website visitors may start this speaker. Only meaningful
    /// for a speaker that is already allowed; the store enforces that.
    /// </summary>
    [HttpPost("{id}/guest")]
    public IActionResult SetGuestAllowed(string id, [FromQuery] bool value)
    {
        bool found = false;
        CastConfigStore.Update(_cfg, c =>
        {
            var s = c.Speakers.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (s != null) { s.GuestAllowed = value && s.Allowed; found = true; }
        });
        if (!found) return NotFound(new { error = "unknown speaker" });
        return Ok(new { id, guestAllowed = value, devices = _cast.Known() });
    }

    /// <summary>Starts the live stream on a speaker.</summary>
    [HttpPost("{id}/play")]
    public async Task<IActionResult> Play(string id, CancellationToken ct)
    {
        var r = await _cast.PlayAsync(id, ct);
        return r.Ok
            ? Ok(new { ok = true, message = r.Message, streamUrl = r.StreamUrl, devices = _cast.Known() })
            : UnprocessableEntity(new { ok = false, error = r.Message, streamUrl = r.StreamUrl });
    }

    /// <summary>Stops the stream on a speaker.</summary>
    [HttpPost("{id}/stop")]
    public async Task<IActionResult> Stop(string id, CancellationToken ct)
    {
        var r = await _cast.StopAsync(id, ct);
        return Ok(new { ok = true, message = r.Message, devices = _cast.Known() });
    }

    /// <summary>Stops every cast this server started.</summary>
    [HttpPost("stop-all")]
    public async Task<object> StopAll(CancellationToken ct)
    {
        var n = await _cast.StopAllAsync(ct);
        return new { ok = true, stopped = n, devices = _cast.Known() };
    }
}
