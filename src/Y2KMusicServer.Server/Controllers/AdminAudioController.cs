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

    /// <summary>
    /// Dumps the audio black box: every live capture ring (deck taps + the
    /// post-mix ring) is written as a float WAV of its last ~10 s to the data
    /// folder's <c>diagnostics</c> directory. Used by the tray's dump item;
    /// also fired automatically on a detected anomaly when DebugLogging is on.
    /// </summary>
    [HttpPost("blackbox/dump")]
    public BlackBoxDumpDto DumpBlackBox()
        => new() { Files = AudioBlackBox.DumpAll(DataPaths.EnsureDiagnosticsDir(_cfg)) };
}
