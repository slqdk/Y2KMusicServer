using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Y2KMusicServer.Server.Data;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// Moving an instance to another machine (File → Move / backup in admin).
/// See <see cref="InstanceBackup"/> for what an export contains and why an
/// import is staged, confirmed, and applied across a restart.
/// </summary>
[ApiController]
[Route("api/admin/backup")]
public sealed class AdminBackupController : ControllerBase
{
    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly IConfiguration _cfg;
    private readonly IHostApplicationLifetime _life;
    private readonly ILogger<AdminBackupController> _log;

    public AdminBackupController(IDbContextFactory<Y2KDbContext> dbf, IConfiguration cfg,
        IHostApplicationLifetime life, ILogger<AdminBackupController> log)
    {
        _dbf = dbf;
        _cfg = cfg;
        _life = life;
        _log = log;
    }

    /// <summary>Downloads the whole instance as one zip. Safe while playing.</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var zipPath = await InstanceBackup.ExportAsync(_dbf, _cfg, ct);
        _log.LogInformation("Instance exported ({Size:0.0} MB).", new FileInfo(zipPath).Length / 1048576.0);

        // Deleted when the response stream closes, sent or not.
        var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        return File(stream, "application/zip", Path.GetFileName(zipPath));
    }

    /// <summary>The staged import awaiting confirmation, or 204.</summary>
    [HttpGet("import")]
    public IActionResult PendingImport()
    {
        var p = InstanceBackup.Pending(_cfg);
        return p == null ? NoContent() : (IActionResult)Ok(p);
    }

    /// <summary>
    /// Uploads an export (raw zip body, not a form — an export with a big
    /// library or YouTube downloads easily passes the default form limits).
    /// Verifies and stages it; nothing live changes until it is confirmed.
    /// </summary>
    [HttpPost("import")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> Upload(CancellationToken ct)
    {
        try
        {
            var staged = await InstanceBackup.StageAsync(Request.Body, _dbf, _cfg, ct);
            _log.LogInformation("Import staged from {Machine} ({Tracks} tracks, {Playlists} playlists) — awaiting confirmation.",
                staged.Manifest.Machine, staged.Manifest.Tracks, staged.Manifest.Playlists);
            return Ok(staged);
        }
        catch (InvalidDataException ex)
        {
            return UnprocessableEntity(new { error = ex.Message });
        }
    }

    /// <summary>Throws the staged import away.</summary>
    [HttpDelete("import")]
    public IActionResult Discard()
    {
        InstanceBackup.DiscardPending(_cfg);
        return NoContent();
    }

    /// <summary>
    /// Confirms the staged import and restarts the service to apply it. When
    /// not running as a service, <c>restarting</c> is false and the operator
    /// restarts by hand — the import still applies on that next start.
    /// </summary>
    [HttpPost("import/apply")]
    public IActionResult Apply()
    {
        if (!InstanceBackup.Confirm(_cfg))
            return NotFound(new { error = "There is no staged import. Upload the export again." });

        _log.LogWarning("Import confirmed — the library and settings are replaced on the next start.");
        bool restarting = InstanceBackup.ScheduleServiceRestart(_life, _log);
        return Ok(new { restarting });
    }
}
