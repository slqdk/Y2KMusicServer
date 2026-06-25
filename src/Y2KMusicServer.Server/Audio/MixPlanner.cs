using System.Globalization;

namespace Y2KMusicServer.Server.Audio;

/// <summary>
/// Every transition the engine can run. The first three are <b>crossfades</b>
/// (the "Crossfade" section); the last three are the musical <b>moves</b> (the
/// "Mixing" section). The identifier is the UI label word-for-word, so the name
/// can never drift between code and screen.
/// </summary>
public enum Transition
{
    NormalCrossfade,        // plain volume crossfade, no beat alignment
    BeatmatchingCrossfade,  // crossfade with the tempos/phase aligned
    BeatDropCrossfade,      // incoming held silent, then dropped in on the beat
    VocalTease,             // incoming vocal rides over the outgoing instrumental
    BassSwap,               // low end handed from one track to the other on a downbeat
    BassBreakdown           // outgoing stripped to its bassline as the incoming enters
}

/// <summary>
/// One automation event on the transition timeline. Triggers (<see cref="At"/>):
/// "fadeStart", "downbeat", "aSilent", "fadeEnd". <see cref="Iso"/> matches the
/// isolator wire (none / bass / vocal / nobass); null leaves the isolator as-is.
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
/// executor would run. Pure data — nothing here touches the engine. A crossfade
/// (Normal/Beatmatching/BeatDrop) carries no <see cref="Steps"/>; the moves do.</summary>
public sealed class MixPlan
{
    public Transition Strategy { get; init; }
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

    /// <summary>True for the three musical moves (they carry steps); false for a
    /// crossfade. The executor runs steps for a move and a fade for a crossfade.</summary>
    public bool IsMove => Steps.Length > 0;
}

/// <summary>
/// Resolves the transition for a pair from the base mix points
/// (<see cref="MixAnalyser"/>), each track's stored grid, and the cached
/// structure (<see cref="TrackStructure"/>), under the operator's
/// <see cref="MixRules"/>. Pure and side-effect-free.
///
/// Two sections, each with its own auto toggle, compose by priority:
/// <list type="number">
///   <item>a <b>Mixing</b> move (vocal-tease / bass-swap / bass-breakdown) when
///         <see cref="MixRules.MixingAuto"/> is on and one fits the pair;</item>
///   <item>else the best <b>Crossfade</b> (Beat drop / Beatmatching / Normal) when
///         <see cref="MixRules.CrossfadeAuto"/> is on;</item>
///   <item>else a plain <b>Normal Crossfade</b> as the floor.</item>
/// </list>
/// An armed transition is forced by the caller via <paramref name="force"/>,
/// which bypasses both sections and the preconditions.
/// </summary>
public static class MixPlanner
{
    private const double MinTeaseVocalSec = 4.0;   // first vocal segment must be at least this long
    private const double Margin = 0.5;             // seconds of slack on "vocals done by X"
    private const double BassPresentThreshold = 0.18; // mean low-band to treat a window as "bassy"
    private const double IntroPunchBars = 2.0;     // bars of B's intro scanned for a strong downbeat

    public static MixPlan Plan(
        MixPoints basePoints,
        double? aBpm, double? bBpm, double? bPhase,
        TrackStructureData? aStruct, TrackStructureData? bStruct,
        MixRules rules,
        Transition? force = null)
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

        // Operator-armed transition: skip both sections, the preconditions, and
        // the per-move toggles, and build the requested transition directly.
        if (force is Transition forced)
            return PlanForced(forced, outSec, inSec, fade, basePoints.BeatAligned,
                bpmClose, bpmDelta, aBpm, bBpm, bPhase, aStruct, bStruct, rules);

        // 1) Mixing section: a musical move, if it's on and one fits the pair.
        if (rules.MixingAuto)
        {
            var move = TrySelectMove(outSec, inSec, fade, beatmatch, basePoints.BeatAligned,
                bpmClose, bpmDelta, aBpm, bBpm, bPhase, aStruct, bStruct, rules);
            if (move != null) return move;
        }

        // 2) Crossfade section: the best-fitting crossfade for the pair's beats.
        if (rules.CrossfadeAuto)
        {
            var cf = SelectCrossfade(beatmatch, inSec, bBpm, bStruct);
            return BuildCrossfade(cf, outSec, inSec, fade, basePoints.BeatAligned, bpmClose, bpmDelta, bpmKnown, fallback: false);
        }

        // 3) Floor: a plain Normal Crossfade.
        return BuildCrossfade(Transition.NormalCrossfade, outSec, inSec, fade,
            basePoints.BeatAligned, bpmClose, bpmDelta, bpmKnown, fallback: true);
    }

    /// <summary>The Mixing section's pick: a move plan, or null when no move suits
    /// the pair (the caller then falls through to the Crossfade section).</summary>
    private static MixPlan? TrySelectMove(
        double outSec, double inSec, double fade, bool beatmatch, bool beatAligned,
        bool bpmClose, double bpmDelta, double? aBpm, double? bBpm, double? bPhase,
        TrackStructureData? aStruct, TrackStructureData? bStruct, MixRules rules)
    {
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
                return Build(Transition.VocalTease, outSec, teaseIn, fade, beatAligned, bpmClose, bpmDelta, steps,
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
                    return Build(Transition.BassSwap, outSec, inSec, fade, beatAligned, bpmClose, bpmDelta, steps,
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
                return Build(Transition.BassSwap, outSec, inSec, fade, beatAligned, bpmClose, bpmDelta, openSteps,
                    $"bass-swap: A tail bass-light — B enters full, no bass cut; fade {Fmt(fade)}s");
            }

            return null; // beatmatch-capable but no move's preconditions/toggles met
        }

        // Not beatmatch-capable: only the bass-breakdown move applies.
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
            return Build(Transition.BassBreakdown, outSec, inSec, fade, beatAligned, bpmClose, bpmDelta, steps,
                $"bass-breakdown: A to bass-only outro under B; fade {Fmt(fade)}s");
        }

        return null; // no move fits
    }

    /// <summary>The Crossfade section's pick: Beat drop when the tempos align and
    /// B has a strong intro downbeat to drop on; Beatmatching when the tempos
    /// align; otherwise Normal (the floor — also when tempos are too far apart).</summary>
    private static Transition SelectCrossfade(bool beatmatch, double inSec, double? bBpm, TrackStructureData? bStruct)
    {
        if (!beatmatch) return Transition.NormalCrossfade;
        if (BIntroPunchy(bStruct, inSec, bBpm)) return Transition.BeatDropCrossfade;
        return Transition.BeatmatchingCrossfade;
    }

    private static MixPlan BuildCrossfade(
        Transition cf, double outSec, double inSec, double fade,
        bool beatAligned, bool bpmClose, double bpmDelta, bool bpmKnown, bool fallback)
    {
        // Beatmatching / Beat drop ride the beat-aligned points; Normal does not.
        bool aligned = cf != Transition.NormalCrossfade && beatAligned;
        string reason = cf switch
        {
            Transition.BeatDropCrossfade =>
                $"beat-drop crossfade: tempos aligned and B has a strong intro — B dropped in on the beat; fade {Fmt(fade)}s",
            Transition.BeatmatchingCrossfade =>
                $"beatmatching crossfade: tempos aligned; fade {Fmt(fade)}s",
            _ when fallback =>
                bpmKnown ? $"normal crossfade (fallback): no move fit, tempos not aligned (Δbpm {Fmt(bpmDelta)}); fade {Fmt(fade)}s"
                         : $"normal crossfade (fallback): BPM unknown; fade {Fmt(fade)}s",
            _ =>
                bpmKnown ? $"normal crossfade: tempos not aligned (Δbpm {Fmt(bpmDelta)}); fade {Fmt(fade)}s"
                         : $"normal crossfade: BPM unknown; fade {Fmt(fade)}s",
        };
        return Build(cf, outSec, inSec, fade, aligned, bpmClose, bpmDelta, Array.Empty<MixStep>(), reason);
    }

    /// <summary>Builds a specific transition on demand for the operator's arm
    /// buttons — no precondition or toggle gates. Structure data is used when it
    /// is available and falls back to safe defaults, so the chosen transition
    /// always runs. A forced crossfade has empty steps (the engine renders it as a
    /// fade); a forced move carries its steps.</summary>
    private static MixPlan PlanForced(
        Transition transition, double outSec, double inSec, double fade,
        bool beatAligned, bool bpmClose, double bpmDelta,
        double? aBpm, double? bBpm, double? bPhase,
        TrackStructureData? aStruct, TrackStructureData? bStruct, MixRules rules)
    {
        switch (transition)
        {
            case Transition.VocalTease:
            {
                double teaseIn = bStruct?.VocalStartSec is double v
                    ? SnapToGrid(v, bBpm, bPhase) : inSec;
                var steps = new[]
                {
                    new MixStep { At = "fadeStart", Deck = "B", Iso = "vocal", Vol = rules.DeckBEntryLevel,
                                  Note = "forced vocal-tease: B vocal-only over A's tail" },
                    new MixStep { At = "aSilent", Deck = "B", Iso = "none", Vol = 1.0,
                                  Note = "A gone — drop B's vocal filter, B to full" },
                };
                return Build(Transition.VocalTease, outSec, teaseIn, fade, beatAligned, bpmClose, bpmDelta, steps,
                    $"forced vocal-tease: B vocal@{Fmt(teaseIn)}s; fade {Fmt(fade)}s");
            }
            case Transition.BassSwap:
            {
                double holdSec = BarsToSec(rules.BassHoldBars, aBpm);
                var steps = new[]
                {
                    new MixStep { At = "fadeStart", Deck = "B", Iso = "nobass", Vol = rules.DeckBEntryLevel,
                                  Note = "forced bass-swap: B enters with bass killed" },
                    new MixStep { At = "downbeat", Deck = "A", Iso = "nobass", Note = "swap: kill A's bass" },
                    new MixStep { At = "downbeat", Deck = "B", Iso = "none", Vol = 1.0,
                                  Note = "swap: restore B's bass and bring B to full" },
                };
                return Build(Transition.BassSwap, outSec, inSec, fade, beatAligned, bpmClose, bpmDelta, steps,
                    $"forced bass-swap: hold {rules.BassHoldBars} bar(s) then swap on a downbeat; fade {Fmt(fade)}s",
                    swapHoldSec: holdSec);
            }
            case Transition.BassBreakdown:
            {
                var steps = new[]
                {
                    new MixStep { At = "fadeStart", Deck = "A", Iso = "bass",
                                  Note = "forced bass-breakdown: strip A to bass-only" },
                    new MixStep { At = "fadeStart", Deck = "B", Vol = rules.DeckBEntryLevel,
                                  Note = "B enters over A's bass-only outro" },
                    new MixStep { At = "aSilent", Deck = "B", Vol = 1.0, Note = "A gone — B to full" },
                };
                return Build(Transition.BassBreakdown, outSec, inSec, fade, beatAligned, bpmClose, bpmDelta, steps,
                    $"forced bass-breakdown: A to bass-only outro under B; fade {Fmt(fade)}s");
            }
            case Transition.BeatmatchingCrossfade:
                return Build(Transition.BeatmatchingCrossfade, outSec, inSec, fade, beatAligned, bpmClose, bpmDelta,
                    Array.Empty<MixStep>(), $"forced beatmatching crossfade; fade {Fmt(fade)}s");
            case Transition.BeatDropCrossfade:
                return Build(Transition.BeatDropCrossfade, outSec, inSec, fade, beatAligned, bpmClose, bpmDelta,
                    Array.Empty<MixStep>(), $"forced beat-drop crossfade; fade {Fmt(fade)}s");
            default:
                return Build(Transition.NormalCrossfade, outSec, inSec, fade, beatAligned: false, bpmClose, bpmDelta,
                    Array.Empty<MixStep>(), $"forced normal crossfade; fade {Fmt(fade)}s");
        }
    }

    private static MixPlan Build(Transition s, double outSec, double inSec, double fade,
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

    /// <summary>True if B's low band is strongly present in the first couple of
    /// bars from its in-point — a clear downbeat/kick to "drop" B in on. No data →
    /// false (don't force a beat-drop without evidence; fall back to beatmatching).</summary>
    private static bool BIntroPunchy(TrackStructureData? b, double inSec, double? bBpm)
    {
        if (b == null || b.Bass.Length == 0 || b.WindowSec <= 0) return false;
        double barSec = bBpm is double bb && bb > 0 ? (60.0 / bb) * 4.0 : 2.0;
        double winEnd = inSec + IntroPunchBars * barSec;
        int i0 = Math.Max(0, (int)Math.Floor(inSec / b.WindowSec));
        int i1 = Math.Min(b.Bass.Length, (int)Math.Ceiling(winEnd / b.WindowSec));
        if (i1 <= i0) return false;
        long sum = 0;
        for (int i = i0; i < i1; i++) sum += b.Bass[i];
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
