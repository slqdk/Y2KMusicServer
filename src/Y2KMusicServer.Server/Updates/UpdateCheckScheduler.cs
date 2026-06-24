namespace Y2KMusicServer.Server.Updates;

/// <summary>
/// Periodically nudges <see cref="GitHubUpdateChecker"/> to refresh
/// the cached <c>UpdateInfoDto</c>. Default interval is 24 hours,
/// overridable via <c>Updates:CheckIntervalHours</c> in
/// appsettings. Also runs one check ~30 seconds after startup so
/// the tray sees up-to-date status promptly without paying its
/// cost on the critical path.
/// </summary>
public sealed class UpdateCheckScheduler : BackgroundService
{
    private readonly GitHubUpdateChecker _checker;
    private readonly TimeSpan _interval;
    private readonly ILogger<UpdateCheckScheduler> _log;

    public UpdateCheckScheduler(GitHubUpdateChecker checker,
                                IConfiguration cfg,
                                ILogger<UpdateCheckScheduler> log)
    {
        _checker = checker;
        _log = log;
        var hours = cfg.GetValue<double?>("Updates:CheckIntervalHours") ?? 24.0;
        if (hours < 0.1) hours = 0.1; // floor at 6 min so dev tweaks don't hammer GitHub
        _interval = TimeSpan.FromHours(hours);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _checker.CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Scheduled update check failed");
            }
            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
