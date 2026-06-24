using System.Text.Json;
using NAudio.Dsp;
using Y2KMusicServer.Server.Data;

namespace Y2KMusicServer.Server.Audio;

/// <summary>
/// Audio-derived structure of one track, for the auto-mix planner: an energy
/// envelope, a vocal-presence curve, the sung segments derived from it, the
/// instrumental intro/outro boundaries, and coarse drop markers. Cached on disk
/// (<c>data\structure\&lt;trackId&gt;.json</c>).
///
/// What is deliberately NOT here: the beat / phrase grid. That is derived live
/// from the (mutable) <c>Bpm</c> / <c>BeatPhaseOffsetSec</c> on the Track row, so
/// a beat-grid edit never invalidates this file — the same separation
/// <see cref="WaveformData"/> keeps for the waveform.
///
/// Vocal detection is a heuristic, not stem separation: per window it scores the
/// centre channel's vocal-band (~200 Hz–4 kHz) energy and how *centred* that band
/// is (vocals sit centre; wide instruments score lower). It locates the rough
/// vocal entry well but over-flags centre-heavy mid-band instrumentals and misses
/// breathy vocals. Clean isolation is the deferred ML-stems path.
/// </summary>
public sealed class TrackStructureData
{
    /// <summary>Schema version. Bumped when the algorithm changes so stale
    /// caches recompute on next request.</summary>
    public int Version { get; init; }

    public int SampleRate { get; init; }

    /// <summary>Seconds summarised by each <see cref="Energy"/> / <see cref="Vocal"/>
    /// entry. <c>time = index * WindowSec</c>.</summary>
    public double WindowSec { get; init; }

    public double DurationSec { get; init; }

    /// <summary>Per-window overall energy, normalised to the track's peak and
    /// quantised 0..255.</summary>
    public int[] Energy { get; init; } = Array.Empty<int>();

    /// <summary>Per-window vocal-presence score, 0..255 (centre-band energy ×
    /// how centred it is). A curve, so the threshold can be re-tuned later.</summary>
    public int[] Vocal { get; init; } = Array.Empty<int>();

    /// <summary>Per-window low-band (kick / bassline) energy, 0..255, scaled by the
    /// same peak as <see cref="Energy"/> — so it reads as "share of the track's peak
    /// loudness that sits in the low end" at each window. Lets the planner ask
    /// whether A's tail still has bass to clash with B's. A curve; threshold later.</summary>
    public int[] Bass { get; init; } = Array.Empty<int>();

    /// <summary>Sung segments derived from <see cref="Vocal"/> (gated by energy,
    /// gaps bridged, short blips dropped).</summary>
    public Segment[] VocalSegments { get; init; } = Array.Empty<Segment>();

    /// <summary>Start of the first vocal segment, or null if none — the natural
    /// in-point for a vocal-tease mix.</summary>
    public double? VocalStartSec { get; init; }

    /// <summary>Where the instrumental intro gives way to the body (first
    /// sustained energy).</summary>
    public double IntroEndSec { get; init; }

    /// <summary>Where the body gives way to the outro (after the last sustained
    /// energy).</summary>
    public double OutroStartSec { get; init; }

    /// <summary>Coarse energy step-ups (build → drop / chorus entry), in seconds.
    /// Heuristic; may be empty.</summary>
    public double[] DropMarkers { get; init; } = Array.Empty<double>();

    public sealed record Segment(double StartSec, double EndSec);
}

/// <summary>
/// Computes <see cref="TrackStructureData"/> for a track and caches it on disk.
/// Lazy: the structure endpoint builds it on first request and reads the cache
/// thereafter, mirroring <see cref="WaveformPeaks"/>. Decoded through
/// <see cref="SafeAudioFileReader"/> (so FLAC rides on Media Foundation).
/// </summary>
public static class TrackStructure
{
    public const int SchemaVersion = 2;

    private const double WindowSec = 0.25;

    // Vocal band: a gentle band-pass applied to centre and side streams.
    private const float BandLowHz = 200f;
    private const float BandHighHz = 4000f;
    private const float Q = 0.7071f;

    // Low band for the "is there a kick / bassline here?" measure — a 2-pole
    // low-pass cascade on the centre stream, near the bass isolator's corner.
    private const float BassHz = 150f;

    // Vocal-segment derivation (on the 0..1 domain before quantisation).
    private const double VocalThreshold = 0.33;   // vocal-score floor
    private const double EnergyGate = 0.12;        // normalised-energy floor to count as vocal
    private const double MergeGapSec = 1.0;        // bridge silences shorter than this
    private const double MinSegmentSec = 1.5;      // drop segments shorter than this

    private const float Eps = 1e-7f;

    /// <summary>
    /// Returns the cached structure for a track, computing and caching it on a
    /// miss (or a version bump). Throws if the file cannot be opened/decoded
    /// (caller maps to 404).
    /// </summary>
    public static TrackStructureData GetOrBuild(IConfiguration cfg, int trackId, string filePath)
    {
        var dir = DataPaths.EnsureStructureDir(cfg);
        var file = Path.Combine(dir, trackId + ".json");

        if (File.Exists(file))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<TrackStructureData>(File.ReadAllText(file));
                if (cached != null && cached.Version == SchemaVersion && cached.Energy.Length > 0)
                    return cached;
            }
            catch
            {
                // Corrupt / old-version cache — fall through and recompute.
            }
        }

        var data = Compute(filePath);

        try { File.WriteAllText(file, JsonSerializer.Serialize(data)); }
        catch { /* cache write is best-effort; serving still works */ }

        return data;
    }

    /// <summary>
    /// Decodes <paramref name="filePath"/> and derives the structure. Throws on an
    /// unreadable file (the SafeAudioFileReader ctor throws).
    /// </summary>
    public static TrackStructureData Compute(string filePath)
    {
        using var reader = new SafeAudioFileReader(filePath);

        int channels = reader.WaveFormat.Channels;
        int sampleRate = reader.WaveFormat.SampleRate;
        if (channels <= 0 || sampleRate <= 0)
            return Empty(sampleRate);

        int windowSamps = Math.Max(1, (int)Math.Round(sampleRate * WindowSec));

        // Band-pass (HP then LP) on the centre stream and, separately, the side
        // stream — so we can score both vocal-band energy and how centred it is.
        var hpC = BiQuadFilter.HighPassFilter(sampleRate, BandLowHz, Q);
        var lpC = BiQuadFilter.LowPassFilter(sampleRate, BandHighHz, Q);
        var hpS = BiQuadFilter.HighPassFilter(sampleRate, BandLowHz, Q);
        var lpS = BiQuadFilter.LowPassFilter(sampleRate, BandHighHz, Q);

        // Low band: two cascaded low-passes (~24 dB/oct) on the centre stream.
        var lpB1 = BiQuadFilter.LowPassFilter(sampleRate, BassHz, Q);
        var lpB2 = BiQuadFilter.LowPassFilter(sampleRate, BassHz, Q);

        var energyRaw = new List<double>(4096);
        var vocalRaw = new List<double>(4096);
        var bassRaw = new List<double>(4096);

        var buf = new float[8192 * channels];
        double sumFull = 0, sumCMid = 0, sumSMid = 0, sumBass = 0;
        int frameInWindow = 0;
        long framesRead = 0;
        int read;

        while ((read = reader.Read(buf, 0, buf.Length)) > 0)
        {
            int frames = read / channels;
            for (int f = 0; f < frames; f++)
            {
                int baseIdx = f * channels;
                float left = buf[baseIdx];
                float right = channels > 1 ? buf[baseIdx + 1] : left;
                float centre = 0.5f * (left + right);
                float side = 0.5f * (left - right);

                float cMid = lpC.Transform(hpC.Transform(centre));
                float sMid = lpS.Transform(hpS.Transform(side));
                float low = lpB2.Transform(lpB1.Transform(centre));

                sumFull += centre * (double)centre;
                sumCMid += cMid * (double)cMid;
                sumSMid += sMid * (double)sMid;
                sumBass += low * (double)low;

                if (++frameInWindow >= windowSamps)
                {
                    EmitWindow(energyRaw, vocalRaw, bassRaw, sumFull, sumCMid, sumSMid, sumBass, frameInWindow);
                    sumFull = sumCMid = sumSMid = sumBass = 0;
                    frameInWindow = 0;
                }
            }
            framesRead += frames;
        }

        if (frameInWindow > 0)
            EmitWindow(energyRaw, vocalRaw, bassRaw, sumFull, sumCMid, sumSMid, sumBass, frameInWindow);

        double durationSec = framesRead / (double)sampleRate;

        int n = energyRaw.Count;
        if (n == 0) return Empty(sampleRate);

        // Normalise energy to the track's 95th-percentile peak.
        double peak = Percentile(energyRaw, 0.95);
        if (peak < Eps) peak = Eps;
        var energyNorm = new double[n];
        for (int i = 0; i < n; i++) energyNorm[i] = Clamp01(energyRaw[i] / peak);

        var energyQ = new int[n];
        var vocalQ = new int[n];
        var bassQ = new int[n];
        for (int i = 0; i < n; i++)
        {
            energyQ[i] = (int)Math.Round(energyNorm[i] * 255.0);
            vocalQ[i] = (int)Math.Round(Clamp01(vocalRaw[i]) * 255.0);
            bassQ[i] = (int)Math.Round(Clamp01(bassRaw[i] / peak) * 255.0);
        }

        var segments = DeriveSegments(vocalRaw, energyNorm);
        double? vocalStart = segments.Length > 0 ? segments[0].StartSec : (double?)null;
        (double introEnd, double outroStart) = IntroOutro(energyNorm, durationSec);
        var drops = DeriveDrops(energyNorm);

        return new TrackStructureData
        {
            Version = SchemaVersion,
            SampleRate = sampleRate,
            WindowSec = WindowSec,
            DurationSec = durationSec,
            Energy = energyQ,
            Vocal = vocalQ,
            Bass = bassQ,
            VocalSegments = segments,
            VocalStartSec = vocalStart,
            IntroEndSec = introEnd,
            OutroStartSec = outroStart,
            DropMarkers = drops
        };
    }

    private static void EmitWindow(
        List<double> energyRaw, List<double> vocalRaw, List<double> bassRaw,
        double sumFull, double sumCMid, double sumSMid, double sumBass, int n)
    {
        double full = Math.Sqrt(sumFull / n);
        double cMid = Math.Sqrt(sumCMid / n);
        double sMid = Math.Sqrt(sumSMid / n);
        double bass = Math.Sqrt(sumBass / n);

        double centredness = cMid / (cMid + sMid + Eps);   // 1 if mono / fully centred
        double midFrac = cMid / (full + Eps);                // share of energy that is centre mid-band
        double vocalScore = Clamp01(midFrac * (0.5 + 0.5 * centredness));

        energyRaw.Add(full);
        vocalRaw.Add(vocalScore);
        bassRaw.Add(bass);
    }

    private static TrackStructureData.Segment[] DeriveSegments(List<double> vocalRaw, double[] energyNorm)
    {
        int n = vocalRaw.Count;
        var active = new bool[n];
        for (int i = 0; i < n; i++)
            active[i] = vocalRaw[i] >= VocalThreshold && energyNorm[i] >= EnergyGate;

        int mergeGapWins = (int)Math.Round(MergeGapSec / WindowSec);
        int minSegWins = (int)Math.Round(MinSegmentSec / WindowSec);

        var segs = new List<TrackStructureData.Segment>();
        int i2 = 0;
        while (i2 < n)
        {
            if (!active[i2]) { i2++; continue; }

            int start = i2;
            int end = i2;
            int j = i2 + 1;
            int gap = 0;
            while (j < n)
            {
                if (active[j]) { end = j; gap = 0; }
                else if (++gap > mergeGapWins) break;
                j++;
            }

            if (end - start + 1 >= minSegWins)
                segs.Add(new TrackStructureData.Segment(start * WindowSec, (end + 1) * WindowSec));

            i2 = j;
        }
        return segs.ToArray();
    }

    private static (double introEnd, double outroStart) IntroOutro(double[] energyNorm, double durationSec)
    {
        int n = energyNorm.Length;
        double body = BodyMedian(energyNorm);
        double introThr = body * 0.40;
        double outroThr = body * 0.60;

        int introIdx = 0;
        for (int i = 0; i < n; i++)
            if (energyNorm[i] >= introThr) { introIdx = i; break; }

        int outroIdx = n - 1;
        for (int i = n - 1; i >= 0; i--)
            if (energyNorm[i] >= outroThr) { outroIdx = i; break; }

        double introEnd = Math.Min(introIdx * WindowSec, durationSec);
        double outroStart = Math.Min((outroIdx + 1) * WindowSec, durationSec);
        return (introEnd, outroStart);
    }

    private static double[] DeriveDrops(double[] energyNorm)
    {
        int n = energyNorm.Length;
        if (n < 8) return Array.Empty<double>();

        // Smooth over ~1 s, then mark a sustained step-up of >=0.30 vs ~2 s back.
        int smoothWins = Math.Max(1, (int)Math.Round(1.0 / WindowSec));
        int backWins = Math.Max(1, (int)Math.Round(2.0 / WindowSec));
        int suppressWins = (int)Math.Round(8.0 / WindowSec);

        var ma = new double[n];
        double acc = 0;
        for (int i = 0; i < n; i++)
        {
            acc += energyNorm[i];
            if (i >= smoothWins) acc -= energyNorm[i - smoothWins];
            ma[i] = acc / Math.Min(i + 1, smoothWins);
        }

        var drops = new List<double>();
        int lastMark = -suppressWins;
        for (int i = backWins; i < n; i++)
        {
            if (ma[i] >= 0.60 && ma[i] - ma[i - backWins] >= 0.30 && i - lastMark >= suppressWins)
            {
                drops.Add(i * WindowSec);
                lastMark = i;
            }
        }
        return drops.ToArray();
    }

    private static double BodyMedian(double[] energy)
    {
        int n = energy.Length;
        int start = n / 4, end = n * 3 / 4;
        int len = end - start;
        if (len <= 0) return Percentile(new List<double>(energy), 0.5);

        var slice = new double[len];
        Array.Copy(energy, start, slice, 0, len);
        Array.Sort(slice);
        return slice[len / 2];
    }

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.ToArray();
        Array.Sort(sorted);
        int idx = (int)Math.Min(sorted.Length - 1, sorted.Length * p);
        return sorted[idx];
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static TrackStructureData Empty(int sampleRate) => new()
    {
        Version = SchemaVersion,
        SampleRate = sampleRate > 0 ? sampleRate : 1,
        WindowSec = WindowSec,
        DurationSec = 0
    };
}
