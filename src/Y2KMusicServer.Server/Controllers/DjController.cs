using Microsoft.AspNetCore.Mvc;
using Y2KMusicServer.Server.Audio;
using Y2KMusicServer.Server.Data;
using Y2KMusicServer.Server.Playback;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// The mobile DJ page (<c>/DJAdmin</c>): the handful of controls a DJ needs
/// while standing in the crowd with a phone — talk over the music, fade out and
/// back, jump to the next song, see and prune the next few queue entries, and
/// flip which playlists Auto DJ is drawing from.
///
/// Everything here already exists elsewhere in the admin API; this controller
/// exists so the phone makes ONE call per screen refresh instead of five, and
/// so the duck/fade behaviour has a home that isn't the desktop transport.
/// </summary>
[ApiController]
[Route("api/dj")]
public sealed class DjController : ControllerBase
{
    private readonly AudioEngine _engine;
    private readonly PlaylistService _playlist;
    private readonly IConfiguration _cfg;
    private readonly ILogger<DjController> _log;

    /// <summary>How many upcoming entries the phone shows.</summary>
    private const int QueuePeek = 5;

    public DjController(AudioEngine engine, PlaylistService playlist, IConfiguration cfg, ILogger<DjController> log)
    {
        _engine = engine;
        _playlist = playlist;
        _cfg = cfg;
        _log = log;
    }

    /// <summary>Everything the phone screen needs, in one poll.</summary>
    [HttpGet("state")]
    public async Task<object> State(CancellationToken ct)
    {
        var s = _engine.GetStatus();
        var (gain, ducked, fadePaused) = _engine.DuckState();
        var audio = AudioConfigStore.Load(_cfg);

        var all = await _playlist.GetAsync(ct);
        var playhead = _playlist.PlayedThroughEntryId();
        var headIdx = playhead > 0 ? all.ToList().FindIndex(e => e.Id == playhead) : -1;
        if (headIdx < 0 && s.TrackId is int tid)
            headIdx = all.ToList().FindIndex(e => e.TrackId == tid);

        var upcoming = all.Skip(headIdx + 1).Take(QueuePeek)
            .Select(e => new { e.Id, e.TrackId, e.Title, e.Artist, e.DurationSec, e.Source, e.AddedBy })
            .ToList();

        var feeds = AutoDjFeedStore.Load(_cfg);
        var now = DateTime.Now;
        var playlists = (await _playlist.SavedPlaylistsWithSlotsAsync(ct))
            .Select(pl => new
            {
                pl.Id,
                pl.Name,
                Feed = feeds.Contains(pl.Id),
                ScheduledNow = PlaylistService.IsPlaylistActiveNow(pl, now),
                TrackCount = pl.Tracks.Count
            })
            .ToList();

        return new
        {
            playing = s.State == PlaybackEngineState.Playing,
            s.TrackId,
            s.Title,
            s.Artist,
            s.PositionSec,
            s.DurationSec,
            s.Crossfading,
            duckGain = gain,
            ducked,
            fadePaused,
            duckLevelPercent = audio.DuckLevelPercent,
            fadeSeconds = audio.FadeSeconds,
            upcoming,
            playlists
        };
    }

    public sealed record OnBody(bool On);

    /// <summary>
    /// Talk-over: held down while the DJ speaks. Ramps to the configured level
    /// and back over the configured fade, both directions.
    /// </summary>
    [HttpPost("duck")]
    public IActionResult Duck([FromBody] OnBody? body)
    {
        var audio = AudioConfigStore.Load(_cfg);
        _engine.SetDuck(body?.On ?? false, audio.DuckLevelPercent / 100.0, audio.FadeSeconds);
        return Ok(new { ducked = body?.On ?? false, audio.DuckLevelPercent, audio.FadeSeconds });
    }

    /// <summary>Fading pause: ramps to silence and pauses; off plays and ramps back.</summary>
    [HttpPost("fade-pause")]
    public IActionResult FadePause([FromBody] OnBody? body)
    {
        var audio = AudioConfigStore.Load(_cfg);
        _engine.SetFadePause(body?.On ?? false, audio.FadeSeconds);
        return Ok(new { fadePaused = body?.On ?? false });
    }

    /// <summary>
    /// Start the transition to the next queued song as a plain Normal crossfade
    /// — no beat matching, no mixing move, so the result is predictable when
    /// you're triggering it by hand from the dance floor.
    /// </summary>
    [HttpPost("next")]
    public async Task<IActionResult> Next(CancellationToken ct)
    {
        _engine.ArmTransition(Transition.NormalCrossfade);
        var r = await _engine.NextAsync(null, ct);
        return r == QueueResult.Ok
            ? Ok(new { ok = true })
            : UnprocessableEntity(new { ok = false, error = r.ToString() });
    }

    /// <summary>Drops one upcoming entry from the queue.</summary>
    [HttpDelete("queue/{entryId:int}")]
    public async Task<IActionResult> RemoveEntry(int entryId, CancellationToken ct)
    {
        var ok = await _playlist.RemoveAsync(entryId, ct);
        return ok ? Ok(new { removed = entryId }) : NotFound();
    }

    /// <summary>Turns Auto DJ feeding on/off for one playlist.</summary>
    [HttpPost("feed/{playlistId:int}")]
    public IActionResult SetFeed(int playlistId, [FromQuery] bool value)
    {
        AutoDjFeedStore.Set(_cfg, playlistId, value);
        _log.LogInformation("DJ page set Auto DJ feed for playlist {Id} to {Value}.", playlistId, value);
        return Ok(new { playlistId, feed = value });
    }

    public sealed record DuckSettingsBody(int? LevelPercent, double? FadeSeconds);

    /// <summary>Adjusts the talk-over level and the ramp length.</summary>
    [HttpPost("duck-settings")]
    public IActionResult DuckSettings([FromBody] DuckSettingsBody? body)
    {
        var audio = AudioConfigStore.Load(_cfg);
        if (body?.LevelPercent is int lp) audio.DuckLevelPercent = Math.Clamp(lp, 0, 100);
        if (body?.FadeSeconds is double fs) audio.FadeSeconds = Math.Clamp(fs, 0.2, 30);
        audio = AudioConfigStore.Save(_cfg, audio);
        return Ok(new { audio.DuckLevelPercent, audio.FadeSeconds });
    }
}
