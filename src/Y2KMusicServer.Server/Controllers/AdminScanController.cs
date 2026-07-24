using Microsoft.AspNetCore.Mvc;
using Y2KMusicServer.Server.Scanning;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// Admin scan control. Kicks the <see cref="LibraryScanner"/> and reports its
/// state. Live progress is also pushed over SignalR (<c>scanProgress</c> /
/// <c>scanComplete</c>); this endpoint exists so the state is pollable without
/// a hub connection.
/// </summary>
[ApiController]
[Route("api/admin/scan")]
public sealed class AdminScanController : ControllerBase
{
    private readonly LibraryScanner _scanner;

    public AdminScanController(LibraryScanner scanner) => _scanner = scanner;

    /// <summary>
    /// Starts a scan. Optional <c>folderId</c> (a global scan-folder id) scans
    /// that single folder; omit it to scan the whole folder list. Always 202 —
    /// requests queue FIFO behind a running scan.
    /// </summary>
    [HttpPost]
    public IActionResult Start([FromQuery] int? folderId)
        => _scanner.TryStart(folderId)
            ? Accepted(_scanner.Current)
            : Conflict(_scanner.Current);

    [HttpGet("status")]
    public ScanProgress Status() => _scanner.Current;
}
