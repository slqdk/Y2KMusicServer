namespace Y2KMusicServer.Server.Audio;

/// <summary>
/// Pure crossfade ramp math, separated from the engine so it can be unit
/// tested without any audio. The EOF contract here is policy (a): keep the
/// trigger point and shorten the fade so Deck B reaches full volume exactly by
/// Deck A's end — never overrun.
/// </summary>
public static class CrossfadeMath
{
    /// <summary>Absolute floor so a fade never collapses to a divide-by-zero.</summary>
    public const double MinFadeSec = 0.10;

    /// <summary>
    /// The fade length actually used, given where the fade starts
    /// (<paramref name="triggerSec"/>), the configured length, and the track's
    /// true end. If the configured fade would run past the end, it is shortened
    /// to land exactly at the end (policy a).
    /// </summary>
    public static double EffectiveFadeSec(double triggerSec, double configuredFadeSec, double trackEndSec)
    {
        var room = trackEndSec - triggerSec;
        if (room <= MinFadeSec) return MinFadeSec;          // at/over the edge → near-cut
        if (configuredFadeSec <= room) return configuredFadeSec;
        return room;                                        // shorten to fit
    }

    /// <summary>True when the configured fade had to be shortened to fit.</summary>
    public static bool WasShortened(double triggerSec, double configuredFadeSec, double trackEndSec)
        => configuredFadeSec > (trackEndSec - triggerSec) + 1e-9
           && (trackEndSec - triggerSec) > MinFadeSec;

    /// <summary>Per-tick increment so that <c>pos</c> climbs 0→1 over the fade.</summary>
    public static double StepPerTick(double tickMs, double fadeSec)
        => fadeSec <= 0 ? 1.0 : tickMs / (fadeSec * 1000.0);

    /// <summary>Deck A volume at fade progress <paramref name="pos"/> (1→0).</summary>
    public static float VolA(float startVolA, double pos)
        => (float)Math.Max(0.0, startVolA * (1.0 - pos));

    /// <summary>Deck B volume at fade progress <paramref name="pos"/> (0→target).</summary>
    public static float VolB(float targetVolB, double pos)
        => (float)Math.Min(targetVolB, targetVolB * Math.Max(0.0, pos));
}
