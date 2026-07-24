using Microsoft.AspNetCore.Mvc;
using Y2KMusicServer.Server.Data;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// The operator-editable genre map (<see cref="GenreMapStore"/>): buckets +
/// raw-tag→bucket rules, applied at query time so a PUT re-buckets the whole
/// library instantly with no rescan. GET returns the map; PUT replaces it
/// whole (the UI editor works on the full document).
/// </summary>
[ApiController]
[Route("api/admin/genre-map")]
public sealed class AdminGenreMapController : ControllerBase
{
    private readonly IConfiguration _cfg;

    public AdminGenreMapController(IConfiguration cfg) => _cfg = cfg;

    [HttpGet]
    public GenreMapStore.GenreMap Get() => GenreMapStore.Load(_cfg);

    [HttpPut]
    public IActionResult Put([FromBody] GenreMapStore.GenreMap? body)
    {
        if (body == null) return UnprocessableEntity(new { error = "map body required" });
        var saved = GenreMapStore.Save(_cfg, body);
        return Ok(saved);
    }
}
