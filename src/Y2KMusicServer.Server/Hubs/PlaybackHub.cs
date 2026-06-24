using Microsoft.AspNetCore.SignalR;

namespace Y2KMusicServer.Server.Hubs;

/// <summary>
/// Server → client push channel for the admin page. In later
/// phases this carries now-playing, VU samples, deck progress,
/// scan progress, and log events. In Phase 0 it just sends a
/// "hello" on connect and a tick every 5 seconds, so frontend
/// integration is testable end-to-end before the audio engine
/// lands.
/// </summary>
public sealed class PlaybackHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("hello", new
        {
            connectionId = Context.ConnectionId,
            serverTimeUtc = DateTime.UtcNow,
            phase = "phase-0-scaffold"
        });
        await base.OnConnectedAsync();
    }
}
