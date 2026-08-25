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

        var feeds = AutoDjFeedStore.LoadState(_cfg);
        var now = DateTime.Now;
        // The jingle playlist gets its own tab; it is never an Auto DJ toggle.
        var jingleId = JingleStore.PlaylistId(_cfg);
        var playlists = (await _playlist.SavedPlaylistsWithSlotsAsync(ct))
            .Where(pl => pl.Id != jingleId)
            .Select(pl => new
            {
                pl.Id,
                pl.Name,
                Feed = feeds.IsActive(pl, now, PlaylistService.IsPlaylistActiveNow),
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
            trimPercent = (int)Math.Round(_engine.Trim * 100),
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

    /// <summary>
    /// Drops one upcoming entry from the queue. The FIRST upcoming entry is
    /// normally the one already cued on Deck B, so the cue is dropped with it —
    /// otherwise the deleted song still crossfades in on the trigger and the
    /// delete looks ignored. The scheduler re-arms from the queue on its next
    /// tick.
    /// </summary>
    [HttpDelete("queue/{entryId:int}")]
    public async Task<IActionResult> RemoveEntry(int entryId, CancellationToken ct)
    {
        var trackId = await _playlist.TrackIdOfEntryAsync(entryId, ct);
        var ok = await _playlist.RemoveAsync(entryId, ct);
        if (!ok) return NotFound();

        bool uncued = trackId is int tid && _engine.CancelPreparedIfTrack(tid);
        _log.LogInformation("DJ page removed queue entry {EntryId}{Uncued}.",
            entryId, uncued ? " (and cleared the cue)" : "");
        return Ok(new { removed = entryId, uncued });
    }

    /// <summary>Turns Auto DJ feeding on/off for one playlist.</summary>
    [HttpPost("feed/{playlistId:int}")]
    public IActionResult SetFeed(int playlistId, [FromQuery] bool value)
    {
        AutoDjFeedStore.Set(_cfg, playlistId, value);
        _log.LogInformation("DJ page set Auto DJ feed for playlist {Id} to {Value}.", playlistId, value);
        return Ok(new { playlistId, feed = value });
    }

    public sealed record SelectionBody(List<int>? PlaylistIds);

    /// <summary>
    /// Sets the live playlist selection. Five seconds after the last change the
    /// queue is swept and refilled from it, and playback crossfades into the
    /// first new song. Empty list = back to the timeslots. Always available
    /// here, whatever the website is allowed to do.
    /// </summary>
    [HttpPost("selection")]
    public async Task<IActionResult> SetSelection([FromBody] SelectionBody? body, CancellationToken ct)
    {
        var ids = body?.PlaylistIds ?? new List<int>();
        await _playlist.SetLiveSelectionAsync(ids, ct);
        return Ok(new { selected = ids, swapInSec = 5 });
    }

    /// <summary>
    /// The jingles the DJ can fire: the designated playlist's tracks, in
    /// playlist order. Empty when no playlist is designated, so the phone can
    /// show an explanation rather than an empty grid it can't account for.
    /// </summary>
    [HttpGet("jingles")]
    public async Task<object> Jingles(CancellationToken ct)
    {
        var jingleId = JingleStore.PlaylistId(_cfg);
        if (jingleId is not int id)
            return new { designated = false, name = (string?)null, items = Array.Empty<object>() };

        var (name, tracks) = await _playlist.JingleTracksAsync(id, ct);
        return new
        {
            designated = true,
            name,
            items = tracks.Select(t => new { t.Id, t.Title, t.Artist, t.DurationSec }).ToList()
        };
    }

    /// <summary>
    /// Fires a jingle: cue it on Deck B and crossfade immediately — the same
    /// path as the desktop's Play now, with a Normal crossfade armed so a
    /// hand-fired jingle never turns into a beat-drop or a bass swap. What was
    /// playing is abandoned, exactly as Next does; when the jingle ends the
    /// queue carries on as usual.
    /// </summary>
    [HttpPost("jingles/{trackId:int}")]
    public async Task<IActionResult> FireJingle(int trackId, CancellationToken ct)
    {
        var status = _engine.GetStatus();
        if (status.State != PlaybackEngineState.Playing || status.TrackId == null)
        {
            // Nothing on air to mix out of: load it and start.
            var loaded = await _engine.LoadAsync(trackId, ct);
            if (loaded != LoadResult.Ok)
                return UnprocessableEntity(new { ok = false, error = loaded.ToString() });
            _engine.Play();
            _log.LogInformation("DJ page started jingle track {TrackId} from stopped.", trackId);
            return Ok(new { ok = true, started = true });
        }

        _engine.ArmTransition(Transition.NormalCrossfade);
        var cue = await _engine.QueueNextAsync(trackId, ct, manual: true);
        if (cue != QueueResult.Ok)
            return UnprocessableEntity(new { ok = false, error = cue.ToString() });
        if (!_engine.CrossfadeNow())
            return Conflict(new { ok = false, error = "already crossfading" });

        _log.LogInformation("DJ page fired jingle track {TrackId}.", trackId);
        return Ok(new { ok = true, started = false });
    }

    /// <summary>
    /// Queues a jingle instead of firing it: it lands where a hand-picked track
    /// lands — just before the next Auto DJ entry, so it plays after the current
    /// song rather than interrupting it. Nothing about the current track changes.
    /// </summary>
    [HttpPost("jingles/{trackId:int}/queue")]
    public async Task<IActionResult> QueueJingle(int trackId, CancellationToken ct)
    {
        var current = _engine.GetStatus().TrackId;
        var r = await _playlist.AddAsync(trackId, PlaylistSource.Manual, "Jingle", current, ct);
        if (r != PlaylistAddResult.Ok)
            return UnprocessableEntity(new { ok = false, error = r.ToString() });

        _log.LogInformation("DJ page queued jingle track {TrackId}.", trackId);
        return Ok(new { ok = true });
    }

    /// <summary>
    /// Starts the music: resumes a paused deck, or with an empty deck picks the
    /// queue back up (topping up from Auto DJ first if the queue has run dry).
    /// Same capability as the desktop's Play — the phone should not be the one
    /// console that cannot start a show.
    /// </summary>
    [HttpPost("play")]
    public async Task<IActionResult> Play(CancellationToken ct)
    {
        if (_engine.GetStatus().State == PlaybackEngineState.Playing)
            return Ok(new { ok = true, alreadyPlaying = true });

        // A deck parked at the end of its track can't be resumed — advance past
        // it instead, or Play just replays the same end-of-file.
        if (!_engine.DeckSpent && _engine.Play()) { _log.LogInformation("DJ page resumed playback."); return Ok(new { ok = true }); }

        var (upcomingId, _) = await _playlist.NextUpcomingAsync(null, ct);
        var resumeId = _engine.DeckSpent
            ? upcomingId ?? await _playlist.ResumeTrackIdAsync(ct)
            : await _playlist.ResumeTrackIdAsync(ct);
        if (resumeId == null && await _playlist.IsAutoDjOnAsync(ct))
        {
            await _playlist.TopUpAsync(ct);
            resumeId = await _playlist.ResumeTrackIdAsync(ct);
        }
        if (resumeId is not int trackId)
            return Conflict(new { ok = false, error = "nothing to play" });

        if (await _engine.LoadAsync(trackId, ct) != LoadResult.Ok)
            return Conflict(new { ok = false, error = "could not load the track" });

        _engine.Play();
        _log.LogInformation("DJ page started playback at track {TrackId}.", trackId);
        return Ok(new { ok = true, started = true });
    }

    /// <summary>Stops the music. The queue and the playhead are untouched, so
    /// Play picks up where this left off.</summary>
    [HttpPost("stop")]
    public IActionResult Stop()
    {
        _engine.Stop();
        _log.LogInformation("DJ page stopped playback.");
        return Ok(new { ok = true });
    }

    /// <summary>
    /// Queues any library track from the phone's search — it lands where a
    /// hand-picked track lands, just before the next Auto DJ entry, so it plays
    /// after the current song rather than interrupting it.
    /// </summary>
    [HttpPost("queue/{trackId:int}")]
    public async Task<IActionResult> QueueTrack(int trackId, CancellationToken ct)
    {
        var current = _engine.GetStatus().TrackId;
        var r = await _playlist.AddAsync(trackId, PlaylistSource.Manual, "DJ", current, ct);
        if (r != PlaylistAddResult.Ok)
            return UnprocessableEntity(new { ok = false, error = r.ToString() });

        _log.LogInformation("DJ page queued track {TrackId}.", trackId);
        return Ok(new { ok = true });
    }

    public sealed record TrimBody(int Percent);

    /// <summary>
    /// Sets the live volume trim: 10–100% of the master volume, applied to the
    /// output immediately (ramped, not stepped). Master itself is untouched, so
    /// nothing here changes what the next track is built at.
    /// </summary>
    [HttpPost("trim")]
    public IActionResult Trim([FromBody] TrimBody? body)
    {
        int pct = Math.Clamp(body?.Percent ?? 100, 10, 100);
        var audio = AudioConfigStore.Load(_cfg);
        _engine.SetTrim(pct / 100.0, Math.Max(0.2, audio.FadeSeconds / 2.0));
        _log.LogInformation("DJ page set volume trim to {Percent}% of master.", pct);
        return Ok(new { trimPercent = pct });
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
