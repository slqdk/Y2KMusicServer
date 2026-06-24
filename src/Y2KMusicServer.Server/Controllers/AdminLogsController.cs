using Microsoft.AspNetCore.Mvc;
using Serilog.Events;
using Y2KMusicServer.Server.Diagnostics;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// Reads the in-memory log ring buffer for the admin page. The live feed is the
/// <c>logEntry</c> hub event; this endpoint is the on-load / pollable snapshot
/// (and works without a hub connection). Read-only — verbosity is changed via
/// the <c>DebugLogging</c> flag on <c>PUT /api/admin/settings</c>.
/// </summary>
[ApiController]
[Route("api/admin/logs")]
public sealed class AdminLogsController : ControllerBase
{
    private readonly LogRingBuffer _buffer;
    private readonly LogVerbositySwitch _verbosity;

    public AdminLogsController(LogRingBuffer buffer, LogVerbositySwitch verbosity)
    {
        _buffer = buffer;
        _verbosity = verbosity;
    }

    /// <summary>
    /// Recent entries, oldest first. <c>take</c> caps the count (default 200,
    /// max = buffer capacity); <c>level</c> filters to a minimum level
    /// (Verbose / Debug / Information / Warning / Error / Fatal).
    /// </summary>
    [HttpGet]
    public IActionResult Get([FromQuery] int take = 200, [FromQuery] string? level = null)
    {
        var min = ParseLevel(level);
        take = Math.Clamp(take, 1, LogRingBuffer.Capacity);
        return Ok(new
        {
            level = _verbosity.CurrentLevel,
            debugEnabled = _verbosity.DebugEnabled,
            capacity = LogRingBuffer.Capacity,
            entries = _buffer.Snapshot(take, min)
        });
    }

    /// <summary>Current pipeline minimum level and whether verbose (Debug) is on.</summary>
    [HttpGet("level")]
    public IActionResult Level() => Ok(new
    {
        level = _verbosity.CurrentLevel,
        debugEnabled = _verbosity.DebugEnabled
    });

    private static LogEventLevel ParseLevel(string? s)
        => Enum.TryParse<LogEventLevel>(s, ignoreCase: true, out var l) ? l : LogEventLevel.Verbose;
}
