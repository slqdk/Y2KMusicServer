using Microsoft.AspNetCore.Mvc;
using Y2KMusicServer.Server.Audio;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// Admin control for the audio-analysis pass (Phase 5). Kicks
/// <see cref="AudioAnalysisService"/> and reports its state. Progress is also
/// pushed over SignalR (<c>analyzeProgress</c> / <c>analyzeComplete</c>); this
/// endpoint exists so the state is pollable without a hub connection.
/// </summary>
[ApiController]
[Route("api/admin/analyze")]
public sealed class AdminAnalyzeController : ControllerBase
{
    private readonly AudioAnalysisService _analysis;

    public AdminAnalyzeController(AudioAnalysisService analysis) => _analysis = analysis;

    /// <summary>
    /// Starts a pass. <c>all=true</c> re-measures every track; otherwise only
    /// tracks without loudness are analysed. 202 on start, 409 if already running.
    /// </summary>
    [HttpPost]
    public IActionResult Start([FromQuery] bool all = false)
        => _analysis.TryStart(all)
            ? Accepted(_analysis.Current)
            : Conflict(_analysis.Current);

    [HttpGet("status")]
    public AnalysisProgress Status() => _analysis.Current;
}
