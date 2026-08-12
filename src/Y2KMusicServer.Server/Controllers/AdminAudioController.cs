using Microsoft.AspNetCore.Mvc;
using Y2KMusicServer.Server.Audio;
using Y2KMusicServer.Server.Data;
using Y2KMusicServer.Shared;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// Local-audio (server-machine speakers) control and diagnostics. Used by the
/// tray's Local audio toggle. GET reports the operator flag, the default
/// render device as the service process sees it, and each live deck's actual
/// output; POST flips the flag, persists it (audio-config.json), and rebuilds
/// the live decks' outputs in place.
/// </summary>
[ApiController]
[Route("api/admin/audio")]
public sealed class AdminAudioController : ControllerBase
{
    private readonly AudioEngine _engine;
    private readonly IConfiguration _cfg;

    public AdminAudioController(AudioEngine engine, IConfiguration cfg)
    {
        _engine = engine;
        _cfg = cfg;
    }

    [HttpGet("local")]
    public LocalAudioStatusDto GetLocal() => _engine.GetLocalAudioStatus();

    [HttpPost("local")]
    public LocalAudioStatusDto SetLocal([FromQuery] bool enabled)
    {
        AudioConfigStore.Save(_cfg, new AudioConfigStore.AudioConfig { LocalAudioEnabled = enabled });
        _engine.SetLocalAudio(enabled);
        return _engine.GetLocalAudioStatus();
    }
}
