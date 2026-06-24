using Microsoft.AspNetCore.SignalR;
using Y2KMusicServer.Server.Diagnostics;

namespace Y2KMusicServer.Server.Hubs;

/// <summary>
/// Forwards each captured log line to the <c>PlaybackHub</c> as a <c>logEntry</c>
/// event, so the admin page shows a live log without polling. Same broadcaster
/// pattern as <c>AnalysisHubBroadcaster</c> / <c>PlaybackBroadcaster</c>; the
/// engine and the Serilog sink stay free of any SignalR dependency.
///
/// Framework namespaces are pinned to Warning in Program.cs, so SignalR's own
/// per-send logging cannot feed back through the sink into another send.
/// </summary>
public sealed class LogHubBroadcaster : IHostedService
{
    private readonly LogRingBuffer _buffer;
    private readonly IHubContext<PlaybackHub> _hub;

    public LogHubBroadcaster(LogRingBuffer buffer, IHubContext<PlaybackHub> hub)
    {
        _buffer = buffer;
        _hub = hub;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _buffer.Emitted += OnEmitted;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _buffer.Emitted -= OnEmitted;
        return Task.CompletedTask;
    }

    private void OnEmitted(LogEntryDto entry)
    {
        // Fire-and-forget; never throw back into the logging path.
        try { _ = _hub.Clients.All.SendAsync("logEntry", entry); }
        catch { /* no listeners / transient hub error — ignore */ }
    }
}
