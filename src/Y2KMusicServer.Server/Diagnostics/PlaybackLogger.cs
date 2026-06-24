using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Audio;
using Y2KMusicServer.Server.Data;

namespace Y2KMusicServer.Server.Diagnostics;

/// <summary>
/// Turns the engine's event surface into a readable activity log. It subscribes
/// to the same events <c>PlaybackBroadcaster</c> uses, so the engine keeps no
/// logging or database dependency of its own. It writes one Information line
/// when a new track starts — attributed to its source (Operator / AutoDj /
/// Schedule / Request, read from the track's surviving playlist entry) — and one
/// when a crossfade begins, reusing the engine's TransitionInfo (fade length,
/// Smart Mix / beat alignment, and the analyser's reason string).
///
/// Source is best-effort: it is resolved from the current playlist entry, which
/// <c>PruneConsumedAsync</c> keeps alive while the track is playing. A track
/// played off-playlist (an operator Load of an id that is not in the playlist)
/// has no entry and is logged as Operator.
/// </summary>
public sealed class PlaybackLogger : IHostedService
{
    private readonly AudioEngine _engine;
    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly ILogger<PlaybackLogger> _log;

    private int? _lastTrackId;

    public PlaybackLogger(AudioEngine engine, IDbContextFactory<Y2KDbContext> dbf, ILogger<PlaybackLogger> log)
    {
        _engine = engine;
        _dbf = dbf;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _engine.NowPlayingChanged += OnNowPlaying;
        _engine.TransitionStarted += OnTransition;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _engine.NowPlayingChanged -= OnNowPlaying;
        _engine.TransitionStarted -= OnTransition;
        return Task.CompletedTask;
    }

    private void OnNowPlaying(NowPlayingInfo info)
    {
        // Only log a "started" line on an actual change to a real track — not on
        // pause / resume / stop, which also raise this event.
        if (info.TrackId is not int id) { _lastTrackId = null; return; }
        if (id == _lastTrackId) return;
        _lastTrackId = id;

        string title = string.IsNullOrWhiteSpace(info.Title) ? $"track {id}" : info.Title!;
        string artist = string.IsNullOrWhiteSpace(info.Artist) ? "unknown artist" : info.Artist!;

        // DB lookup off the engine's event thread.
        _ = Task.Run(async () =>
        {
            string source = await ResolveSourceAsync(id);
            _log.LogInformation("Now playing: {Title} by {Artist} [source: {Source}]", title, artist, source);
        });
    }

    private void OnTransition(TransitionInfo t)
    {
        _ = Task.Run(async () =>
        {
            var (fromTitle, toTitle) = await ResolveTitlesAsync(t.FromTrackId, t.ToTrackId);
            string mode = t.SmartMix ? "Smart Mix" : "fade";
            string beat = t.BeatAligned ? ", beat-aligned" : "";
            string shortened = t.FadeShortened ? ", fade shortened to fit" : "";
            _log.LogInformation(
                "Crossfade: {From} -> {To} ({Mode} {Fade:F1}s{Beat}{Shortened}) [{Reason}]",
                fromTitle, toTitle, mode, t.FadeSeconds, beat, shortened, t.Reason ?? "no reason");
        });
    }

    private async Task<string> ResolveSourceAsync(int trackId)
    {
        try
        {
            await using var db = await _dbf.CreateDbContextAsync();
            // Nullable cast distinguishes "no entry" (off-playlist operator load)
            // from a real entry whose Source happens to be the enum default.
            var src = await db.PlaylistEntries.AsNoTracking()
                .Where(e => e.TrackId == trackId)
                .OrderBy(e => e.Position)
                .Select(e => (PlaylistSource?)e.Source)
                .FirstOrDefaultAsync();
            return Label(src);
        }
        catch { return "Operator"; }
    }

    private async Task<(string from, string to)> ResolveTitlesAsync(int? fromId, int? toId)
    {
        try
        {
            await using var db = await _dbf.CreateDbContextAsync();
            return (await TitleAsync(db, fromId), await TitleAsync(db, toId));
        }
        catch { return (fromId?.ToString() ?? "?", toId?.ToString() ?? "?"); }
    }

    private static async Task<string> TitleAsync(Y2KDbContext db, int? id)
    {
        if (id is not int v) return "(none)";
        var title = await db.Tracks.AsNoTracking()
            .Where(t => t.Id == v).Select(t => t.Title).FirstOrDefaultAsync();
        return string.IsNullOrWhiteSpace(title) ? $"track {v}" : title!;
    }

    // Persisted enum -> operator-facing source label. Manual is the operator's
    // own pick; Auto is the Auto DJ fallback fill; Schedule is a time-slot-driven
    // fill; Request is an accepted listener request. No entry -> Operator.
    private static string Label(PlaylistSource? s) => s switch
    {
        PlaylistSource.Manual => "Operator",
        PlaylistSource.Auto => "AutoDj",
        PlaylistSource.Schedule => "Schedule",
        PlaylistSource.Request => "Request",
        _ => "Operator"
    };
}
