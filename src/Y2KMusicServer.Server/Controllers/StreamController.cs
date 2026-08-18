using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Y2KMusicServer.Server.Streaming;

namespace Y2KMusicServer.Server.Controllers;

/// <summary>
/// The live broadcast endpoint and its small admin control surface. The
/// broadcast is always on — there is no enable switch; the only setting is
/// the MP3 bitrate.
///
/// <para><c>GET /stream</c> serves the mixed output of both decks. Format is
/// chosen by the <c>format</c> query parameter:</para>
/// <list type="bullet">
///   <item><c>/stream</c> (or anything other than <c>mp3</c>) → WAV/PCM16
///   (<c>audio/wav</c>); a 44-byte streaming header is sent once, then raw
///   PCM.</item>
///   <item><c>/stream?format=mp3</c> → MP3 (<c>audio/mpeg</c>) from the shared
///   LAME encoder at <c>Settings.StreamingBitrate</c>; no header, raw
///   frames.</item>
/// </list>
///
/// <para>WAV is the faithful, dependency-free default; MP3 is the smaller,
/// browser-friendlier path. Both run off the same single mix, so they can be
/// compared side by side.</para>
/// </summary>
[ApiController]
public sealed class StreamController : ControllerBase
{
    private readonly StreamingEncoder _enc;
    private readonly ILogger<StreamController> _log;
    private readonly IHostApplicationLifetime _life;

    public StreamController(StreamingEncoder enc, ILogger<StreamController> log, IHostApplicationLifetime life)
    {
        _enc = enc;
        _log = log;
        _life = life;
    }

    [HttpGet("/stream")]
    public async Task Stream([FromQuery] string? format)
    {
        // A broadcast response never completes on its own, so Kestrel's graceful
        // shutdown would sit here waiting for it: with a listener attached (a
        // browser tab, a Cast speaker) the host hit its shutdown timeout and the
        // process died with OperationCanceledException instead of stopping
        // cleanly — which the installer then tripped over. Linking
        // ApplicationStopping into the loop's token ends every stream the
        // instant shutdown begins. Cancelling here is not an error path: the
        // catch below already treats cancellation as a normal disconnect.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            HttpContext.RequestAborted, _life.ApplicationStopping);
        var ct = linked.Token;

        bool mp3 = string.Equals(format, "mp3", StringComparison.OrdinalIgnoreCase);

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = mp3 ? "audio/mpeg" : "audio/wav";
        Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
        // Unbuffered: every chunk must hit the wire immediately, or players
        // underrun and disconnect.
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var listener = _enc.AddListener(mp3 ? StreamFormat.Mp3 : StreamFormat.Wav);
        try
        {
            if (!mp3)
            {
                var header = _enc.BuildWavHeader();
                await Response.Body.WriteAsync(header, ct);
                await Response.Body.FlushAsync(ct);
            }

            while (!ct.IsCancellationRequested)
            {
                var chunk = await listener.TakeNextAsync(ct);
                if (chunk == null) break;
                await Response.Body.WriteAsync(chunk, ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* client went away */ }
        catch (Exception ex) { _log.LogDebug(ex, "Stream client write ended."); }
        finally
        {
            listener.Dispose();
            _log.LogInformation("Stream listener disconnected ({Format}).",
                mp3 ? "Mp3" : "Wav");
        }
    }

    [HttpGet("/api/admin/stream/status")]
    public StreamStatus Status() => _enc.GetStatus();

    [HttpPost("/api/admin/stream/bitrate")]
    public async Task<IActionResult> Bitrate([FromQuery] int kbps, CancellationToken ct)
    {
        await _enc.SetBitrateAsync(kbps, ct);
        return Ok(_enc.GetStatus());
    }
}
