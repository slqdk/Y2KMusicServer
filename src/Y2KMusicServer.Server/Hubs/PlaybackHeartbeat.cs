using Microsoft.AspNetCore.SignalR;

namespace Y2KMusicServer.Server.Hubs;

/// <summary>
/// Heartbeat hosted service. Phase 0 only. Pushes a tick down
/// the PlaybackHub every 5 seconds so the frontend can verify
/// the WebSocket is alive without any real audio engine yet.
/// Remove (or repurpose) once the real engine events flow.
/// </summary>
public sealed class PlaybackHeartbeat : BackgroundService
{
    private readonly IHubContext<PlaybackHub> _hub;
    private readonly ILogger<PlaybackHeartbeat> _log;

    public PlaybackHeartbeat(IHubContext<PlaybackHub> hub, ILogger<PlaybackHeartbeat> log)
    {
        _hub = hub;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Playback heartbeat started (phase-0 scaffold)");
        var n = 0L;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _hub.Clients.All.SendAsync("tick", new
                {
                    n = ++n,
                    serverTimeUtc = DateTime.UtcNow
                }, stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Heartbeat send failed (no listeners?)");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
