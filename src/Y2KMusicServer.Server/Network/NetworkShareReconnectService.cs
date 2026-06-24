using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Data;

namespace Y2KMusicServer.Server.Network;

/// <summary>
/// On startup, re-authenticates the service's session to the servers behind any
/// network (<c>\\server\share</c>) category folders, using the stored
/// credentials. WNet sessions do not survive a service restart, so without this
/// a network-stored track could not be read by playback or the analysis pass
/// until a browse or scan happened to re-establish the session. The scanner and
/// the folder picker also call <see cref="NetworkShareConnector.EnsureConnected"/>
/// defensively, so this is belt-and-suspenders for the from-boot read paths.
///
/// Runs off the host-start path (as a <see cref="BackgroundService"/>) so a slow
/// or offline share can't delay the service starting. Best-effort: a host that
/// won't authenticate is logged and skipped.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class NetworkShareReconnectService : BackgroundService
{
    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly NetworkShareConnector _connector;
    private readonly ILogger<NetworkShareReconnectService> _log;

    public NetworkShareReconnectService(
        IDbContextFactory<Y2KDbContext> dbf,
        NetworkShareConnector connector,
        ILogger<NetworkShareReconnectService> log)
    {
        _dbf = dbf;
        _connector = connector;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let DB init + the rest of startup settle first.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        List<string> shareRoots;
        try
        {
            await using var db = await _dbf.CreateDbContextAsync(stoppingToken);
            var paths = await db.CategoryFolders.Select(f => f.Path).ToListAsync(stoppingToken);

            // Dedupe to one connect per share root, so several folders on the same
            // share don't each pay a connection timeout when the host is offline.
            shareRoots = paths
                .Select(p => NetworkShareConnector.ShareRoot(p))
                .Where(r => r is not null)
                .Select(r => r!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Network reconnect: could not read category folders");
            return;
        }

        if (shareRoots.Count == 0) return;

        _log.LogInformation("Network reconnect: authenticating {Count} share(s)…", shareRoots.Count);
        foreach (var root in shareRoots)
        {
            if (stoppingToken.IsCancellationRequested) break;
            var r = _connector.EnsureConnected(root);
            if (r.Ok)
                _log.LogInformation("Network share ready: {Share}", root);
            else
                _log.LogWarning("Network share not authenticated: {Share} ({Msg})", root, r.Message);
        }
    }
}
