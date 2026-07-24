using Microsoft.AspNetCore.Mvc;
using Y2KMusicServer.Server.Integrations;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// The online genre lookup (<see cref="GenreLookupService"/>): start / stop /
/// status for the background pass that fills untagged tracks' genres from
/// Deezer. The UI polls status while its dialog is open.
/// </summary>
[ApiController]
[Route("api/admin/genre-lookup")]
public sealed class AdminGenreLookupController : ControllerBase
{
    private readonly GenreLookupService _lookup;

    public AdminGenreLookupController(GenreLookupService lookup) => _lookup = lookup;

    /// <summary>202 started, 409 when a pass is already running.</summary>
    [HttpPost("start")]
    public IActionResult Start()
        => _lookup.TryStart() ? Accepted(_lookup.Current) : Conflict(_lookup.Current);

    /// <summary>Stops after the current track finishes.</summary>
    [HttpPost("stop")]
    public IActionResult Stop()
    {
        _lookup.Stop();
        return Ok(_lookup.Current);
    }

    [HttpGet("status")]
    public GenreLookupService.LookupStatus Status() => _lookup.Current;
}
