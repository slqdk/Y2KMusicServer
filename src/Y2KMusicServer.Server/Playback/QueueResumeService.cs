using Y2KMusicServer.Server.Audio;

namespace Y2KMusicServer.Server.Playback;

/// <summary>
/// Cues (but never starts) the queue's next unplayed track when the service
/// comes up, so the deck shows what will play instead of "[ No track loaded ]".
/// Pressing Play then simply starts it — no hunting for the right song after a
/// restart or a power cut.
///
/// Deliberately load-only: a music server that starts playing by itself the
/// moment Windows boots is a nasty surprise at 04:00. The operator still makes
/// the decision; this only prepares it.
///
/// The load is retried a few times because the music lives on an SMB share that
/// may not be reachable in the first seconds after boot (the share connector
/// runs in parallel).
/// </summary>
public sealed class QueueResumeService : BackgroundService
{
    private readonly AudioEngine _engine;
    private readonly PlaylistService _playlist;
    private readonly ILogger<QueueResumeService> _log;

    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
    private const int MaxAttempts = 4;

    public QueueResumeService(AudioEngine engine, PlaylistService playlist, ILogger<QueueResumeService> log)
    {
        _engine = engine;
        _playlist = playlist;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(FirstDelay, ct);

            for (int attempt = 1; attempt <= MaxAttempts && !ct.IsCancellationRequested; attempt++)
            {
                // The operator (or Auto DJ) got there first — leave well alone.
                var status = _engine.GetStatus();
                if (status.TrackId != null) return;

                var resumeId = await _playlist.ResumeTrackIdAsync(ct);
                if (resumeId is not int trackId)
                {
                    _log.LogDebug("Queue resume: nothing to cue (queue empty or fully played).");
                    return;
                }

                var r = await _engine.LoadAsync(trackId, ct);
                if (r == LoadResult.Ok)
                {
                    _log.LogInformation("Queue resume: cued track {TrackId} on Deck A (not started).", trackId);
                    return;
                }

                _log.LogWarning("Queue resume: could not cue track {TrackId} ({Result}), attempt {Attempt}/{Max}.",
                    trackId, r, attempt, MaxAttempts);

                // FileMissing usually means the share isn't mounted yet; the
                // other failures won't fix themselves, so stop early.
                if (r != LoadResult.FileMissing && r != LoadResult.Unreadable) return;
                await Task.Delay(RetryDelay, ct);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Queue resume failed; the deck stays empty until the operator loads something.");
        }
    }
}
