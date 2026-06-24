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
    /// Starts a scan. Optional <c>categoryId</c> scans a single category;
    /// omit it to scan every category that has folders. 202 on start,
    /// 409 if a scan is already running.
    /// </summary>
    [HttpPost]
    public IActionResult Start([FromQuery] int? categoryId)
        => _scanner.TryStart(categoryId)
            ? Accepted(_scanner.Current)
            : Conflict(_scanner.Current);

    [HttpGet("status")]
    public ScanProgress Status() => _scanner.Current;
}
