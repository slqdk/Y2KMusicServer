using System.Globalization;

namespace Y2KMusicServer.Server.Audio;

public enum MixStrategy { PlainCrossfade, VocalTease, BassSwap, BassBreakdown }

/// <summary>
/// One automation event on the transition timeline. PROVISIONAL shape — the
/// phase-4 executor may refine it. Triggers (<see cref="At"/>): "fadeStart",
/// "downbeat", "aSilent", "fadeEnd". <see cref="Iso"/> matches the isolator wire
/// (none / bass / vocal / nobass); null leaves the isolator as-is.
/// <see cref="Vol"/> is a target 0..1, or null to leave volume to the fade ramp.
/// </summary>
public sealed class MixStep
{
    public string At { get; init; } = "";
    public string Deck { get; init; } = "";    // "A" or "B"
    public string? Iso { get; init; }
    public double? Vol { get; init; }
    public string? Note { get; init; }
}

/// <summary>The transition the planner chose, plus the automation timeline an
/// executor would run. Pure data — nothing here touches the engine.</summary>
public sealed class MixPlan
{
    public MixStrategy Strategy { get; init; }
    public string StrategyName => Strategy.ToString();
    public double OutPointSec { get; init; }
    public double InPointSec { get; init; }
    public double FadeSec { get; init; }
    public bool BeatAligned { get; init; }
    public bool BpmClose { get; init; }
    public double BpmDelta { get; init; }       // octave-folded |Δbpm|, or -1 if unknown
    public MixStep[] Steps { get; init; } = Array.Empty<MixStep>();
    /// <summary>Seconds to hold B's bass cut before the downbeat swap (bass-swap
    /// only). 0 means the executor falls back to the fade midpoint.</summary>
    public double SwapHoldSec { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>
/// Chooses a transition strategy for a pair from the base mix points
/// (<see cref="MixAnalyser"/>), each track's stored grid, and the cached
/// structure (<see cref="TrackStructure"/>), under the operator's
/// <see cref="MixRules"/>. Pure and side-effect-free: it decides and emits a
/// <see cref="MixPlan"/>; it never decodes, mutates, or plays anything. The
/// master <see cref="MixRules.Enabled"/> flag is NOT consulted here (that is the
/// executor's gate) so the dry-run can preview; per-strategy toggles do apply.
///
/// Every branch is gated by both its precondition and its toggle, with a plain
/// crossfade as the floor — weak or missing structure data degrades to a safe
/// fade, never a bad mix.
/// </summary>
public static class MixPlanner
{
    private const double MinTeaseVocalSec = 4.0;  // first vocal segment must be at least this long
    private const double Margin = 0.5;            // seconds of slack on "vocals done by X"
    private const double BassPresentThreshold = 0.18; // mean low-band over the overlap to treat A's tail as "bassy"

    public static MixPlan Plan(
        MixPoints basePoints,
        double? aBpm, double? bBpm, double? bPhase,
        TrackStructureData? aStruct, TrackStructureData? bStruct,
        MixRules rules)
    {
        bool bpmKnown = aBpm is double a && a > 0 && bBpm is double b && b > 0;
        double bpmDelta = bpmKnown ? FoldedDelta(aBpm!.Value, bBpm!.Value) : -1;
        bool bpmClose = bpmKnown && bpmDelta <= rules.BpmTolerance;

        // Beatmatch-capable: tempos close enough to stay aligned over a short
        // overlap (no time-stretch yet) AND the analyser actually aligned phase.
        bool beatmatch = bpmClose && basePoints.BeatAligned;

        double outSec = basePoints.OutPoint;
        double inSec = basePoints.InPoint;
        double fade = CapFade(basePoints.FadeDuration, beatmatch, aBpm, rules.MaxOverlapBars);

        if (beatmatch)
        {
            bool teaseReady = rules.VocalTease
                && bStruct?.VocalStartSec is double
                && FirstVocalLongEnough(bStruct)
                && AInstrumentalBefore(aStruct, outSec);

            if (teaseReady)
            {
                double teaseIn = SnapToGrid(bStruct!.VocalStartSec!.Value, bBpm, bPhase);
                var steps = new[]
                {
                    new MixStep { At = "fadeStart", Deck = "B", Iso = "vocal", Vol = rules.DeckBEntryLevel,
                                  Note = "B enters vocal-only over A's instrumental tail" },
                    new MixStep { At = "aSilent", Deck = "B", Iso = "none", Vol = 1.0,
                                  Note = "A gone — drop B's vocal filter, B to full" },
                };
                return Build(MixStrategy.VocalTease, outSec, teaseIn, fade, basePoints.BeatAligned, bpmClose, bpmDelta, steps,
                    $"vocal-tease: B vocal@{Fmt(teaseIn)}s over A instrumental tail; fade {Fmt(fade)}s");
            }

            if (rules.BassSwap)
            {
                if (ATailBassy(aStruct, outSec, fade))
                {
                    double holdSec = BarsToSec(rules.BassHoldBars, aBpm);
                    var steps = new[]
                    {
                        new MixStep { At = "fadeStart", Deck = "B", Iso = "nobass", Vol = rules.DeckBEntryLevel,
                                      Note = "B enters at entry level with bass killed — no low-end clash" },
                        new MixStep { At = "downbeat", Deck = "A", Iso = "nobass", Note = "swap: kill A's bass" },
                        new MixStep { At = "downbeat", Deck = "B", Iso = "none", Vol = 1.0,
                                      Note = "swap: restore B's bass and bring B to full" },
                    };
                    return Build(MixStrategy.BassSwap, outSec, inSec, fade, basePoints.BeatAligned, bpmClose, bpmDelta, steps,
                        $"bass-swap: A tail bassy — B in NoBass, hold {rules.BassHoldBars} bar(s) then swap on a downbeat; fade {Fmt(fade)}s",
                        swapHoldSec: holdSec);
                }

                // A's tail is bass-light: nothing to clash with, so B comes in with
                // its low end intact — no cut, no swap.
                var openSteps = new[]
                {
                    new MixStep { At = "fadeStart", Deck = "B", Vol = rules.DeckBEntryLevel,
                                  Note = "A tail bass-light — B enters at entry level, bass intact" },
                    new MixStep { At = "aSilent", Deck = "B", Vol = 1.0, Note = "A gone — B to full" },
                };
                return Build(MixStrategy.BassSwap, outSec, inSec, fade, basePoints.BeatAligned, bpmClose, bpmDelta, openSteps,
                    $"bass-swap: A tail bass-light — B enters full, no bass cut; fade {Fmt(fade)}s");
            }

            return Build(MixStrategy.PlainCrossfade, outSec, inSec, fade, basePoints.BeatAligned, bpmClose, bpmDelta,
                Array.Empty<MixStep>(), "plain: beatmatch-capable but tease/swap preconditions unmet");
        }

        // Not beatmatch-capable.
        if (rules.BassBreakdown && AInstrumentalOutro(aStruct))
        {
            var steps = new[]
            {
                new MixStep { At = "fadeStart", Deck = "A", Iso = "bass",
                              Note = "strip A to bass-only (breakdown) while B comes in" },
                new MixStep { At = "fadeStart", Deck = "B", Vol = rules.DeckBEntryLevel,
                              Note = "B enters at entry level over A's bass-only outro" },
                new MixStep { At = "aSilent", Deck = "B", Vol = 1.0,
                              Note = "A gone — B to full" },
            };
            return Build(MixStrategy.BassBreakdown, outSec, inSec, fade, basePoints.BeatAligned, bpmClose, bpmDelta, steps,
                $"bass-breakdown: A to bass-only outro under B; fade {Fmt(fade)}s");
        }

        return Build(MixStrategy.PlainCrossfade, outSec, inSec, fade, basePoints.BeatAligned, bpmClose, bpmDelta,
            Array.Empty<MixStep>(),
            bpmKnown ? $"plain: not beat-aligned (Δbpm {Fmt(bpmDelta)})" : "plain: BPM unknown");
    }

    private static MixPlan Build(MixStrategy s, double outSec, double inSec, double fade,
        bool beatAligned, bool bpmClose, double bpmDelta, MixStep[] steps, string reason,
        double swapHoldSec = 0)
        => new()
        {
            Strategy = s,
            OutPointSec = outSec,
            InPointSec = inSec,
            FadeSec = fade,
            BeatAligned = beatAligned,
            BpmClose = bpmClose,
            BpmDelta = bpmDelta,
            Steps = steps,
            SwapHoldSec = swapHoldSec,
            Reason = reason
        };

    private static double CapFade(double fade, bool beatmatch, double? aBpm, int maxBars)
    {
        if (beatmatch && aBpm is double b && b > 0 && maxBars > 0)
        {
            double cap = maxBars * (60.0 / b) * 4; // bars → seconds
            if (fade > cap) return cap;
        }
        return fade;
    }

    private static bool FirstVocalLongEnough(TrackStructureData? s)
        => s != null && s.VocalSegments.Length > 0
           && (s.VocalSegments[0].EndSec - s.VocalSegments[0].StartSec) >= MinTeaseVocalSec;

    /// <summary>A's vocals are done by the mix-out, so B's vocal teases over an
    /// instrumental tail (no two-vocal clash). True if A has no detected vocals.</summary>
    private static bool AInstrumentalBefore(TrackStructureData? a, double outSec)
    {
        if (a == null) return false;
        if (a.VocalSegments.Length == 0) return true;
        return a.VocalSegments[^1].EndSec <= outSec + Margin;
    }

    /// <summary>A's outro is instrumental (last vocal ends by the outro start).</summary>
    private static bool AInstrumentalOutro(TrackStructureData? a)
    {
        if (a == null) return false;
        if (a.VocalSegments.Length == 0) return true;
        return a.VocalSegments[^1].EndSec <= a.OutroStartSec + Margin;
    }

    /// <summary>True if A's low band is meaningfully present across the overlap
    /// window [outSec, outSec+fade] — i.e. there is a bassline/kick to clash with.
    /// Unknown / no data → true, because the safe choice is to cut B's bass.</summary>
    private static bool ATailBassy(TrackStructureData? a, double outSec, double fade)
    {
        if (a == null || a.Bass.Length == 0 || a.WindowSec <= 0) return true;
        int i0 = Math.Max(0, (int)Math.Floor(outSec / a.WindowSec));
        int i1 = Math.Min(a.Bass.Length, (int)Math.Ceiling((outSec + fade) / a.WindowSec));
        if (i1 <= i0) return true;
        long sum = 0;
        for (int i = i0; i < i1; i++) sum += a.Bass[i];
        double mean01 = (double)sum / (i1 - i0) / 255.0;
        return mean01 >= BassPresentThreshold;
    }

    /// <summary>Whole bars to seconds at A's tempo; 0 if bars ≤ 0 or tempo unknown.</summary>
    private static double BarsToSec(int bars, double? bpm)
        => bars > 0 && bpm is double b && b > 0 ? bars * (60.0 / b) * 4.0 : 0.0;

    private static double SnapToGrid(double sec, double? bpm, double? phase)
    {
        if (bpm is not double b || b <= 0) return sec;
        double beat = 60.0 / b;
        double ph = phase is double p ? ((p % beat) + beat) % beat : 0;
        long k = (long)Math.Round((sec - ph) / beat);
        double snapped = ph + k * beat;
        return snapped < 0 ? sec : snapped;
    }

    /// <summary>Octave-folded absolute BPM difference (so 124 vs 62 ≈ 0).</summary>
    private static double FoldedDelta(double a, double b)
    {
        if (a <= 0 || b <= 0) return double.MaxValue;
        double bb = b;
        while (bb < a * 0.75) bb *= 2;
        while (bb > a * 1.5) bb /= 2;
        return Math.Abs(a - bb);
    }

    private static string Fmt(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
}
