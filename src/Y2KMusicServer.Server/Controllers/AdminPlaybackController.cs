using Microsoft.AspNetCore.Mvc;
using Y2KMusicServer.Server.Audio;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// Admin playback control. Single-deck transport plus crossfade transitions.
/// Live now-playing / progress / VU / transition are pushed over SignalR; this
/// controller drives the engine and exposes a pollable status.
/// </summary>
[ApiController]
[Route("api/admin/playback")]
public sealed class AdminPlaybackController : ControllerBase
{
    private readonly AudioEngine _engine;

    public AdminPlaybackController(AudioEngine engine) => _engine = engine;

    [HttpPost("load")]
    public async Task<IActionResult> Load([FromQuery] int trackId, CancellationToken ct)
        => LoadResultToResponse(await _engine.LoadAsync(trackId, ct), trackId);

    [HttpPost("play")]
    public IActionResult Play()
        => _engine.Play() ? Ok(_engine.GetStatus()) : Conflict(new { error = "nothing loaded" });

    [HttpPost("pause")]
    public IActionResult Pause()
        => _engine.Pause() ? Ok(_engine.GetStatus()) : Conflict(new { error = "not playing" });

    [HttpPost("stop")]
    public IActionResult Stop()
        => _engine.Stop() ? Ok(_engine.GetStatus()) : Conflict(new { error = "nothing loaded" });

    [HttpPost("seek")]
    public IActionResult Seek([FromQuery] double seconds)
        => _engine.Seek(seconds) ? Ok(_engine.GetStatus()) : Conflict(new { error = "nothing loaded" });

    /// <summary>Prepares + arms a crossfade to fire at the computed out-point.</summary>
    [HttpPost("queue-next")]
    public async Task<IActionResult> QueueNext([FromQuery] int trackId, CancellationToken ct)
        => QueueResultToResponse(await _engine.QueueNextAsync(trackId, ct), trackId);

    /// <summary>Crossfades now — to the queued track, or to trackId if supplied.</summary>
    [HttpPost("next")]
    public async Task<IActionResult> Next([FromQuery] int? trackId, CancellationToken ct)
        => QueueResultToResponse(await _engine.NextAsync(trackId, ct), trackId);

    /// <summary>Cues a track onto Deck B (loaded silent at its in-point, not yet
    /// running). The operator starts the preview with <c>play-b</c> and mixes with
    /// <c>crossfade</c>.</summary>
    [HttpPost("cue-b")]
    public async Task<IActionResult> CueB([FromQuery] int trackId, CancellationToken ct)
        => QueueResultToResponse(await _engine.QueueNextAsync(trackId, ct, manual: true), trackId);

    /// <summary>Starts the cued Deck B's silent preview pumping (its beat-clock
    /// scrolls). 409 if nothing is cued or the engine isn't playing.</summary>
    [HttpPost("play-b")]
    public IActionResult PlayB()
        => _engine.PlayDeckB()
            ? Ok(_engine.GetStatus())
            : Conflict(new { error = "nothing cued on Deck B, or not playing" });

    /// <summary>Pauses the cued Deck B's silent preview (keeps it cued).</summary>
    [HttpPost("pause-b")]
    public IActionResult PauseB()
        => _engine.PauseDeckB()
            ? Ok(_engine.GetStatus())
            : Conflict(new { error = "Deck B preview is not running" });

    /// <summary>Nudges the cued Deck B's playhead by ms (negative = earlier) to
    /// align its beats with Deck A.</summary>
    [HttpPost("nudge-b")]
    public IActionResult NudgeB([FromQuery] int ms)
        => _engine.NudgeDeckB(ms / 1000.0)
            ? Ok(_engine.GetStatus())
            : Conflict(new { error = "nothing cued on Deck B" });

    /// <summary>Clears the cued Deck B. 409 while a crossfade is running.</summary>
    [HttpPost("eject-b")]
    public IActionResult EjectB()
        => _engine.EjectDeckB()
            ? Ok(_engine.GetStatus())
            : Conflict(new { error = "nothing cued on Deck B, or crossfading" });

    /// <summary>Operator-fired crossfade from Deck A to the cued Deck B.</summary>
    [HttpPost("crossfade")]
    public IActionResult Crossfade()
        => _engine.CrossfadeNow()
            ? Ok(_engine.GetStatus())
            : Conflict(new { error = "nothing cued on Deck B, or already crossfading" });

    /// <summary>Operator-forced crossfade using a specific auto-mix strategy, for
    /// testing each transition type: ?strategy=PlainCrossfade|VocalTease|BassSwap|
    /// BassBreakdown. Bypasses the auto-selection and the master enable flag. 400
    /// on an unknown strategy; 409 when there is nothing to mix.</summary>
    [HttpPost("mix")]
    public IActionResult Mix([FromQuery] string strategy)
    {
        if (!Enum.TryParse<MixStrategy>(strategy, ignoreCase: true, out var s) || !Enum.IsDefined(s))
            return BadRequest(new { error = "unknown strategy", strategy, allowed = Enum.GetNames<MixStrategy>() });
        return _engine.ForceCrossfade(s)
            ? Ok(_engine.GetStatus())
            : Conflict(new { error = "nothing cued on Deck B, or already crossfading" });
    }

    /// <summary>Set Deck A's EQ isolator: ?mode=none|bass|vocal|nobass.</summary>
    [HttpPost("iso-a")]
    public IActionResult IsoA([FromQuery] string mode) => SetIso(mode, deckA: true);

    /// <summary>Set the cued/mixing Deck B's EQ isolator: ?mode=none|bass|vocal|nobass.</summary>
    [HttpPost("iso-b")]
    public IActionResult IsoB([FromQuery] string mode) => SetIso(mode, deckA: false);

    private IActionResult SetIso(string mode, bool deckA)
    {
        if (!TryParseIsoMode(mode, out var m))
            return BadRequest(new { error = "mode must be none, bass, vocal, or nobass", mode });
        bool ok = deckA ? _engine.SetIsolationA(m) : _engine.SetIsolationB(m);
        return ok
            ? Ok(_engine.GetStatus())
            : Conflict(new { error = deckA ? "no track on Deck A" : "nothing cued on Deck B" });
    }

    private static bool TryParseIsoMode(string? s, out IsoMode mode)
    {
        switch (s?.Trim().ToLowerInvariant())
        {
            case "none": mode = IsoMode.None; return true;
            case "bass": mode = IsoMode.Bass; return true;
            case "vocal": mode = IsoMode.Vocal; return true;
            case "nobass": mode = IsoMode.NoBass; return true;
            default: mode = IsoMode.None; return false;
        }
    }

    [HttpGet("status")]
    public PlaybackStatus Status() => _engine.GetStatus();

    private IActionResult LoadResultToResponse(LoadResult r, int trackId) => r switch
    {
        LoadResult.Ok => Ok(_engine.GetStatus()),
        LoadResult.NotFound => NotFound(new { error = "track not found", trackId }),
        LoadResult.FileMissing => UnprocessableEntity(new { error = "file not found on disk", trackId }),
        LoadResult.Unreadable => UnprocessableEntity(new { error = "file could not be opened", trackId }),
        _ => StatusCode(500)
    };

    private IActionResult QueueResultToResponse(QueueResult r, int? trackId) => r switch
    {
        QueueResult.Ok => Ok(_engine.GetStatus()),
        QueueResult.NoCurrent => Conflict(new { error = "nothing playing to mix from" }),
        QueueResult.NotFound => NotFound(new { error = "track not found", trackId }),
        QueueResult.FileMissing => UnprocessableEntity(new { error = "file not found on disk", trackId }),
        QueueResult.Unreadable => UnprocessableEntity(new { error = "file could not be opened", trackId }),
        _ => StatusCode(500)
    };
}
