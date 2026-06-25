// ═══════════════════════════════════════════════════════════════════════════
//  MixAnalyser — joint pair analysis. Decodes an energy envelope (Media
//  Foundation → 22 kHz mono float, 200 ms RMS windows) and finds:
//    OUT-POINT (A): last window still at >=60% of body energy, in the last 35%,
//                   snapped to A's beat grid when BPM is known.
//    IN-POINT  (B): first window rising to >=40% of body energy, <=60 s,
//                   snapped to B's beat grid when BPM is known.
//    PAIR SCORE: proximity-to-end*0.4 + in-energy*0.3 + beat-alignment*0.3.
//  When both tracks have BPM *and* a beat-phase offset (from the Phase 5
//  analysis pass), the grids are anchored at the real first-beat phase rather
//  than assumed to start at t=0, and a candidate search picks the out-point
//  whose beat instant best coincides with B's beat grid at the mix moment.
//  Works without BPM (skips snapping; fade falls back to a short cut).
// ═══════════════════════════════════════════════════════════════════════════

using NAudio.Wave;

namespace Y2KMusicServer.Server.Audio;

public sealed class MixPoints
{
    public double OutPoint { get; set; }
    public double InPoint { get; set; }
    public double Duration { get; set; }
    public double PairScore { get; set; }
    public string? Reason { get; set; }
    public double FadeDuration { get; set; }
    public bool BeatAligned { get; set; }
    public bool IsValid => OutPoint > 0 && Duration > 0;
}

public static class MixAnalyser
{
    private const int SampleRate = 22050;
    private const int WindowMs = 200;
    private const int WindowSamps = SampleRate * WindowMs / 1000;

    /// <summary>
    /// Ideal fade overlap from the BPM relationship: same tempo → sameBars bars;
    /// related tempo → relatedBars bars of the slower; unknown/very different →
    /// 3 s. The bar counts default to 4 / 2 and are operator-configurable
    /// (mixrules.json); they govern the beat-matched crossfades and the moves —
    /// the Normal crossfade is bounded by a seconds cap instead.
    /// </summary>
    public static double SmartFadeDuration(double bpmA, double bpmB, int sameBars = 4, int relatedBars = 2)
    {
        if (bpmA > 30 && bpmB > 30)
        {
            double ratio = bpmA / bpmB;
            if (ratio > 1) ratio = 1.0 / ratio;

            if (ratio >= 0.95)
            {
                double beatSec = 60.0 / bpmA;
                return beatSec * 4 * sameBars; // sameBars bars
            }
            if (ratio >= 0.50)
            {
                double slowerBpm = Math.Min(bpmA, bpmB);
                double beatSec = 60.0 / slowerBpm;
                return beatSec * 4 * relatedBars; // relatedBars bars of the slower
            }
        }
        return 3.0;
    }

    public static MixPoints AnalysePair(
        string pathA, double bpmA, double phaseA,
        string pathB, double bpmB, double phaseB,
        double fadeDuration,
        CancellationToken ct,
        bool smartMode = false,
        int sameBars = 4, int relatedBars = 2)
    {
        try
        {
            double fd = smartMode ? SmartFadeDuration(bpmA, bpmB, sameBars, relatedBars) : fadeDuration;

            float[]? energyA = null, energyB = null;
            double durA = 0, durB = 0;
            Parallel.Invoke(
                () => DecodeEnergy(pathA, ct, out energyA, out durA),
                () => DecodeEnergy(pathB, ct, out energyB, out durB));

            if (ct.IsCancellationRequested) return new MixPoints();
            if (energyA == null || energyA.Length < 20) return new MixPoints();
            if (energyB == null || energyB.Length < 20) return new MixPoints();

            var result = PlanFromEnergy(energyA, durA, bpmA, phaseA, energyB, durB, bpmB, phaseB, fd);
            result.FadeDuration = fd;
            return result;
        }
        catch
        {
            return new MixPoints();
        }
    }

    /// <summary>
    /// Pure mix-point planner over precomputed energy envelopes. Exposed so the
    /// grid-snapping / beat-alignment logic can be unit-tested without decoding
    /// real audio. <paramref name="phaseA"/> / <paramref name="phaseB"/> are the
    /// tracks' beat-phase offsets in seconds (0 when unknown).
    /// </summary>
    public static MixPoints PlanFromEnergy(
        float[] energyA, double durA, double bpmA, double phaseA,
        float[] energyB, double durB, double bpmB, double phaseB,
        double fadeDuration)
    {
        double winSec = WindowMs / 1000.0;

        double outPoint = FindOutPoint(energyA, durA, bpmA, phaseA, fadeDuration);
        double inPoint = FindInPoint(energyB, durB, bpmB, phaseB);

        bool beatAligned = false;
        double bestErr = 0.5;
        if (bpmA > 30 && bpmB > 30)
        {
            double beatA = 60.0 / bpmA;
            double beatB = 60.0 / bpmB;
            double phA = Mod(phaseA, beatA);
            double phB = Mod(phaseB, beatB);
            double floor = durA * 0.65;
            double ceil = durA - fadeDuration - 0.5;

            long centre = (long)Math.Round((outPoint - phA) / beatA);
            double origOut = outPoint;
            double bestOut = outPoint;
            double bestCost = double.MaxValue;
            bestErr = double.MaxValue;

            // Scan ±8 beats around the energy-derived out-point; pick the A beat
            // instant whose phase best coincides with B's beat grid at mix start,
            // breaking ties toward the beat nearest the original out-point.
            for (long b = centre - 8; b <= centre + 8; b++)
            {
                double candidate = phA + b * beatA;
                if (candidate < floor || candidate > ceil) continue;

                double fracA = Mod(candidate - phA, beatA) / beatA;     // ≈0 (on A grid)
                double fracB = Mod(inPoint - phB, beatB) / beatB;       // ≈0 (on B grid)
                double err = Math.Abs(fracA - fracB);
                err = Math.Min(err, 1.0 - err);

                double cost = err + Math.Abs(candidate - origOut) / beatA * 1e-3;
                if (cost < bestCost) { bestCost = cost; bestErr = err; bestOut = candidate; }
            }

            if (bestErr <= 0.5) outPoint = bestOut;
            beatAligned = bestErr < 0.10;
        }

        float peakB = Percentile(energyB, 0.95f); if (peakB < 1e-6f) peakB = 1e-6f;

        double proxScore = Clamp01((outPoint / durA - 0.6) / 0.4);
        int inIdx = Math.Min((int)(inPoint / winSec), energyB.Length - 1);
        double inEnergyScore = Clamp01(energyB[inIdx] / peakB);
        double beatAlignScore = beatAligned ? 1.0 : 0.3;

        double pairScore = proxScore * 0.4 + inEnergyScore * 0.3 + beatAlignScore * 0.3;
        string reason = BuildReason(outPoint, durA, inPoint, bpmA, bpmB, beatAligned, pairScore);

        return new MixPoints
        {
            OutPoint = outPoint,
            InPoint = inPoint,
            Duration = durA,
            PairScore = pairScore,
            BeatAligned = beatAligned,
            FadeDuration = fadeDuration,
            Reason = reason
        };
    }

    private static double FindOutPoint(float[] energy, double dur, double bpm, double phase, double fadeDuration)
    {
        double winSec = WindowMs / 1000.0;
        int n = energy.Length;

        float bodyEnergy = BodyMedian(energy);
        int floorIdx = (int)(n * 0.65);
        int ceilIdx = Math.Max(floorIdx, (int)((dur - fadeDuration - 0.3) / winSec));
        ceilIdx = Math.Min(ceilIdx, n - 1);

        float threshold = bodyEnergy * 0.60f;

        int outIdx = floorIdx;
        for (int i = ceilIdx; i >= floorIdx; i--)
        {
            if (energy[i] >= threshold) { outIdx = i; break; }
        }

        double outSec = outIdx * winSec;

        if (bpm > 30)
        {
            double beatSec = 60.0 / bpm;
            double ph = Mod(phase, beatSec);
            long k = (long)Math.Round((outSec - ph) / beatSec);
            double snapped = ph + k * beatSec;
            if (snapped >= floorIdx * winSec && snapped <= ceilIdx * winSec)
                outSec = snapped;
        }

        outSec = Math.Max(dur * 0.65, Math.Min(outSec, dur - fadeDuration - 0.3));
        return outSec;
    }

    private static double FindInPoint(float[] energy, double dur, double bpm, double phase)
    {
        double winSec = WindowMs / 1000.0;
        int n = energy.Length;

        float bodyEnergy = BodyMedian(energy);
        float threshold = bodyEnergy * 0.40f;

        int maxIdx = Math.Min(n - 1, (int)(60.0 / winSec));

        int inIdx = 0;
        for (int i = 0; i < maxIdx; i++)
        {
            if (energy[i] >= threshold) { inIdx = i; break; }
        }

        double inSec = inIdx * winSec;

        if (bpm > 30)
        {
            double beatSec = 60.0 / bpm;
            double ph = Mod(phase, beatSec);
            long k = (long)Math.Ceiling((inSec - ph) / beatSec);
            double snapped = ph + k * beatSec;
            if (snapped >= 0 && snapped <= 60.0) inSec = snapped;
        }

        return Math.Max(0, Math.Min(inSec, 60.0));
    }

    private static double Mod(double x, double m)
    {
        if (m <= 0) return 0;
        double r = x % m;
        return r < 0 ? r + m : r;
    }

    private static float BodyMedian(float[] energy)
    {
        int n = energy.Length;
        int start = n / 4;
        int end = n * 3 / 4;
        int len = end - start;
        if (len <= 0) return Percentile(energy, 0.5f);

        var slice = new float[len];
        Array.Copy(energy, start, slice, 0, len);
        Array.Sort(slice);
        return slice[len / 2];
    }

    private static void DecodeEnergy(string path, CancellationToken ct, out float[]? energy, out double duration)
    {
        var buf = new List<float>();
        energy = null;
        duration = 0;
        try
        {
            using var reader = new MediaFoundationReader(path);
            using var resampler = new MediaFoundationResampler(
                reader, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1))
            { ResamplerQuality = 6 };

            var provider = resampler.ToSampleProvider();
            var win = new float[WindowSamps];
            while (!ct.IsCancellationRequested)
            {
                int read = provider.Read(win, 0, win.Length);
                if (read == 0) break;
                double sum = 0;
                for (int i = 0; i < read; i++) sum += win[i] * win[i];
                buf.Add((float)Math.Sqrt(sum / read));
            }

            energy = buf.ToArray();
            duration = energy.Length * (WindowMs / 1000.0);
        }
        catch { }
    }

    private static float Percentile(float[] arr, float p)
    {
        if (arr.Length == 0) return 0;
        var sorted = new float[arr.Length];
        Array.Copy(arr, sorted, arr.Length);
        Array.Sort(sorted);
        return sorted[(int)Math.Min(sorted.Length - 1, sorted.Length * p)];
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static string BuildReason(double outSec, double durA, double inSec, double bpmA, double bpmB, bool beatAligned, double score)
    {
        double outPct = outSec / durA * 100;
        string outDesc = outPct >= 85 ? "late outro" : outPct >= 70 ? "mid outro" : "early outro";
        string inDesc = inSec < 2 ? "immediate" : inSec < 8 ? "short intro skip" : inSec < 20 ? "long intro skip" : "deep intro skip";
        string beatDesc = bpmA > 0 && bpmB > 0
            ? $"BPM {bpmA:F0}->{bpmB:F0}{(beatAligned ? " beat-aligned" : "")}"
            : "no BPM";
        return FormattableString.Invariant($"{outDesc} ({outPct:F0}%) -> {inDesc} [{beatDesc}] q:{score:F2}");
    }
}
