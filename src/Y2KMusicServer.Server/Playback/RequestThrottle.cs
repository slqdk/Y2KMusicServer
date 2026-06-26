using System.Collections.Concurrent;

namespace Y2KMusicServer.Server.Playback;

/// <summary>
/// In-memory per-device request throttle for the listener page. Keyed by the
/// device id the listener page sends (a localStorage token), with the caller IP
/// as a fallback. The window length and on/off switch live in
/// <c>web-config.json</c>; only the "last request per device" timestamps live
/// here, and they intentionally reset on a service restart — a rare event
/// against a window measured in minutes.
/// </summary>
public static class RequestThrottle
{
    private static readonly ConcurrentDictionary<string, DateTime> LastRequestUtc = new();

    /// <summary>
    /// Records a request from <paramref name="deviceKey"/>. Returns <c>null</c>
    /// if the request is allowed (stamping it), or the remaining wait if the
    /// device already requested inside <paramref name="window"/>.
    /// </summary>
    public static TimeSpan? Check(string deviceKey, TimeSpan window)
    {
        var now = DateTime.UtcNow;
        TimeSpan? wait = null;
        LastRequestUtc.AddOrUpdate(
            deviceKey,
            _ => now,                       // first request from this device → allow + stamp
            (_, last) =>
            {
                var elapsed = now - last;
                if (elapsed < window) { wait = window - elapsed; return last; }  // too soon → keep stamp
                return now;                 // window elapsed → allow + restamp
            });
        return wait;
    }
}
