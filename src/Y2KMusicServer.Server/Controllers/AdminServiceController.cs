using Microsoft.AspNetCore.Mvc;
using Y2KMusicServer.Server.Updates;
using Y2KMusicServer.Shared;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// Admin / tray-facing service endpoints. Phase 0 surface only.
/// More admin endpoints (settings, scan triggers, playlist
/// mutation, log dump) land in later phases.
/// </summary>
[ApiController]
[Route("api/admin/service")]
public sealed class AdminServiceController : ControllerBase
{
    private readonly GitHubUpdateChecker _updates;
    private readonly IConfiguration _cfg;
    private static readonly DateTime _startedAtUtc = DateTime.UtcNow;

    public AdminServiceController(GitHubUpdateChecker updates, IConfiguration cfg)
    {
        _updates = updates;
        _cfg = cfg;
    }

    [HttpGet("status")]
    public ServiceStatusDto Status()
    {
        var port = ResolveKestrelPort();
        var host = $"http://localhost:{port}";
        return new ServiceStatusDto
        {
            Version = GitHubUpdateChecker.CurrentVersion(),
            StartedAtUtc = _startedAtUtc,
            MachineName = Environment.MachineName,
            KestrelPort = port,
            AdminUrl = $"{host}/admin",
            ListenerUrl = $"{host}/",
            Update = _updates.Latest
        };
    }

    [HttpGet("update")]
    public UpdateInfoDto Update() => _updates.Latest;

    [HttpPost("update/check")]
    public async Task<UpdateInfoDto> CheckNow(CancellationToken ct)
        => await _updates.CheckAsync(ct);

    private int ResolveKestrelPort()
    {
        var url = _cfg["Kestrel:Endpoints:Http:Url"];
        if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Port;
        return 8765;
    }
}
