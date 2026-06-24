using Microsoft.AspNetCore.SignalR;
using Y2KMusicServer.Server.Audio;

namespace Y2KMusicServer.Server.Hubs;

/// <summary>
/// Forwards <see cref="AudioAnalysisService"/> progress to the
/// <c>PlaybackHub</c> as <c>analyzeProgress</c> events (plus a final
/// <c>analyzeComplete</c>). Same broadcaster pattern as <c>ScanHubBroadcaster</c>.
/// </summary>
public sealed class AnalysisHubBroadcaster : IHostedService
{
    private readonly AudioAnalysisService _analysis;
    private readonly IHubContext<PlaybackHub> _hub;
    private readonly ILogger<AnalysisHubBroadcaster> _log;

    public AnalysisHubBroadcaster(
        AudioAnalysisService analysis,
        IHubContext<PlaybackHub> hub,
        ILogger<AnalysisHubBroadcaster> log)
    {
        _analysis = analysis;
        _hub = hub;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _analysis.Progress += OnProgress;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _analysis.Progress -= OnProgress;
        return Task.CompletedTask;
    }

    private void OnProgress(AnalysisProgress p)
    {
        try
        {
            _ = _hub.Clients.All.SendAsync("analyzeProgress", p);
            if (p.State is AnalysisState.Completed or AnalysisState.Failed)
                _ = _hub.Clients.All.SendAsync("analyzeComplete", p);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "analyzeProgress push failed (no listeners?)");
        }
    }
}
