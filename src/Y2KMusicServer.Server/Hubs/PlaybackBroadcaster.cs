using Microsoft.AspNetCore.SignalR;
using Y2KMusicServer.Server.Audio;

namespace Y2KMusicServer.Server.Hubs;

/// <summary>
/// Forwards <see cref="AudioEngine"/> events to the <c>PlaybackHub</c> as
/// <c>nowPlaying</c>, <c>deckProgress</c>, <c>vu</c>, <c>transition</c>, and
/// <c>beat</c>. Keeps the engine free of any SignalR dependency.
/// </summary>
public sealed class PlaybackBroadcaster : IHostedService
{
    private readonly AudioEngine _engine;
    private readonly IHubContext<PlaybackHub> _hub;
    private readonly ILogger<PlaybackBroadcaster> _log;

    public PlaybackBroadcaster(
        AudioEngine engine,
        IHubContext<PlaybackHub> hub,
        ILogger<PlaybackBroadcaster> log)
    {
        _engine = engine;
        _hub = hub;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _engine.NowPlayingChanged += OnNowPlaying;
        _engine.ProgressChanged += OnProgress;
        _engine.VuChanged += OnVu;
        _engine.TransitionStarted += OnTransition;
        _engine.BeatDetected += OnBeat;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _engine.NowPlayingChanged -= OnNowPlaying;
        _engine.ProgressChanged -= OnProgress;
        _engine.VuChanged -= OnVu;
        _engine.TransitionStarted -= OnTransition;
        _engine.BeatDetected -= OnBeat;
        return Task.CompletedTask;
    }

    private void OnNowPlaying(NowPlayingInfo info) => Push("nowPlaying", info);
    private void OnProgress(DeckProgress p) => Push("deckProgress", p);
    private void OnVu(VuSample vu) => Push("vu", vu);
    private void OnTransition(TransitionInfo t) => Push("transition", t);
    private void OnBeat(BeatPulse b) => Push("beat", b);

    private void Push(string method, object payload)
    {
        try
        {
            _ = _hub.Clients.All.SendAsync(method, payload);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "{Method} push failed", method);
        }
    }
}
