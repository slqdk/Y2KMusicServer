using Microsoft.AspNetCore.Mvc;
using Y2KMusicServer.Server.Network;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// Admin endpoints for the network-share credentials the folder picker and the
/// scanner rely on. The service runs as LocalSystem and cannot read a
/// credentialed SMB share on its own; these let the operator store a
/// username / password per server so the service can authenticate before
/// reading.
///
/// SECURITY POSTURE: like the rest of <c>/api/admin</c> this is unauthenticated
/// by design (single-operator LAN tool — see architecture.md). The stored
/// password is DPAPI-encrypted on disk and is NEVER returned by any endpoint
/// here — only host + username are. Credentials are sent in to <c>connect</c>
/// over plain HTTP on the LAN, the same trust assumption as the rest of the
/// admin API; if the LAN is not trusted, the whole admin surface needs auth + TLS.
/// </summary>
[ApiController]
[Route("api/admin/network")]
public sealed class AdminNetworkController : ControllerBase
{
    private readonly IConfiguration _cfg;
    private readonly NetworkShareConnector _connector;

    public AdminNetworkController(IConfiguration cfg, NetworkShareConnector connector)
    {
        _cfg = cfg;
        _connector = connector;
    }

    public sealed record ConnectRequest(string Path, string Username, string Password);
    public sealed record ShareInfo(string Host, string Username);

    /// <summary>Configured hosts + usernames (never passwords).</summary>
    [HttpGet]
    public IActionResult List()
    {
        if (!OperatingSystem.IsWindows())
            return Ok(Array.Empty<ShareInfo>());

        var shares = NetworkShareStore.List(_cfg)
            .Select(s => new ShareInfo(s.Host, s.Username))
            .ToList();
        return Ok(shares);
    }

    /// <summary>
    /// Authenticates to the path's server and, only if that succeeds, stores the
    /// credential (DPAPI-encrypted). On success the share is readable by the
    /// service — browse it via <c>/api/admin/fs?path=...</c>, assign it to a
    /// category, scan. On failure nothing is stored and the Windows error is
    /// returned.
    /// </summary>
    [HttpPost("connect")]
    public IActionResult Connect([FromBody] ConnectRequest req)
    {
        if (!OperatingSystem.IsWindows())
            return StatusCode(501, new { error = "Network shares are only supported on Windows." });
        if (req is null || string.IsNullOrWhiteSpace(req.Path))
            return BadRequest(new { error = "path is required (e.g. \\\\server\\share)" });

        var host = NetworkShareStore.NormaliseHost(req.Path);
        if (host.Length == 0)
            return BadRequest(new { error = "Could not read a server name from that path." });

        // Connect first; only persist the credential if it actually authenticates,
        // so we never store a password that does not work.
        var result = _connector.Connect(req.Path, req.Username ?? "", req.Password ?? "");
        if (!result.Ok)
            return StatusCode(502, new { ok = false, host, error = result.Message });

        NetworkShareStore.Upsert(_cfg, req.Path, req.Username ?? "", req.Password ?? "");
        return Ok(new { ok = true, host, message = result.Message });
    }

    /// <summary>Forgets the stored credential for a host. (Any live session
    /// clears on the next service restart.)</summary>
    [HttpDelete("{host}")]
    public IActionResult Forget(string host)
    {
        if (!OperatingSystem.IsWindows())
            return Ok(new { removed = false });

        var removed = NetworkShareStore.Remove(_cfg, host);
        return Ok(new { removed, host = NetworkShareStore.NormaliseHost(host) });
    }
}
