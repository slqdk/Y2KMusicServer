using Microsoft.AspNetCore.SignalR;
using Y2KMusicServer.Server.Hubs;

namespace Y2KMusicServer.Server.Scanning;

/// <summary>
/// Forwards <see cref="LibraryScanner"/> progress to the <c>PlaybackHub</c> as
/// <c>scanProgress</c> events (plus a final <c>scanComplete</c>). This keeps the
/// scanner free of any SignalR dependency — the broadcaster pattern described
/// in architecture.md, in its first concrete form.
/// </summary>
public sealed class ScanHubBroadcaster : IHostedService
{
    private readonly LibraryScanner _scanner;
    private readonly IHubContext<PlaybackHub> _hub;
    private readonly ILogger<ScanHubBroadcaster> _log;

    public ScanHubBroadcaster(
        LibraryScanner scanner,
        IHubContext<PlaybackHub> hub,
        ILogger<ScanHubBroadcaster> log)
    {
        _scanner = scanner;
        _hub = hub;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _scanner.Progress += OnProgress;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _scanner.Progress -= OnProgress;
        return Task.CompletedTask;
    }

    private void OnProgress(ScanProgress p)
    {
        try
        {
            _ = _hub.Clients.All.SendAsync("scanProgress", p);
            if (p.State is ScanState.Completed or ScanState.Failed)
                _ = _hub.Clients.All.SendAsync("scanComplete", p);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "scanProgress push failed (no listeners?)");
        }
    }
}
