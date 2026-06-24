using NAudio.Dsp;
using NAudio.Wave;

namespace Y2KMusicServer.Server.Audio;

/// <summary>Tempo estimate for a track.</summary>
public sealed record BpmResult(double Bpm, double Confidence, double BeatPhaseOffsetSec);

/// <summary>
/// Tempo + beat-phase estimation. The signal is reduced to a multi-band
/// spectral-flux onset envelope; tempo comes from the autocorrelation of that
/// envelope weighted by a log-Gaussian prior around 120 BPM (which resolves
/// most octave errors); beat phase comes from an Ellis-style dynamic-programming
/// beat track. Fills <c>Track.Bpm</c> / <c>BpmConfidence</c> /
/// <c>BeatPhaseOffsetSec</c>. Replaces the legacy broadband-RMS detector.
///
/// <see cref="Analyze"/> takes an <see cref="ISampleProvider"/> so it can be
/// unit-tested against synthetic click trains without files.
/// </summary>
public sealed class BpmDetector
{
    private const int FftSize = 1024;
    private const int Hop = 512;
    private const int Bands = 6;
    private const double MinBpm = 60.0;
    private const double MaxBpm = 200.0;
    private const double MaxSeconds = 120.0; // analyse up to ~2 minutes

    public BpmResult? AnalyzeFile(string path)
    {
        try { using var r = new SafeAudioFileReader(path); return Analyze(r); }
        catch { return null; }
    }

    public BpmResult? Analyze(ISampleProvider source)
    {
        int fs = source.WaveFormat.SampleRate;
        int channels = source.WaveFormat.Channels;
        if (fs <= 0 || channels <= 0) return null;

        double frameRate = (double)fs / Hop;
        var odf = ComputeOnsetEnvelope(source, channels, frameRate);
        if (odf.Count < 32) return null;

        double energy = 0;
        foreach (var v in odf) energy += v;
        if (energy <= 1e-6) return null; // effectively silent / no onsets

        Normalize(odf);

        int minLag = Math.Max(1, (int)Math.Floor(60.0 * frameRate / MaxBpm));
        int maxLag = Math.Min(odf.Count - 1, (int)Math.Ceiling(60.0 * frameRate / MinBpm));
        if (maxLag <= minLag) return null;

        double bestScore = double.NegativeInfinity, sumScore = 0;
        int bestLag = 0, nScore = 0;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double ac = 0;
            for (int t = 0; t + lag < odf.Count; t++) ac += odf[t] * odf[t + lag];
            double bpm = 60.0 * frameRate / lag;
            double score = ac * TempoPrior(bpm);
            sumScore += score; nScore++;
            if (score > bestScore) { bestScore = score; bestLag = lag; }
        }
        if (bestLag <= 0 || bestScore <= 0) return null;

        double meanScore = nScore > 0 ? sumScore / nScore : 0;
        double confidence = meanScore > 0 ? Clamp01((bestScore / meanScore - 1.0) / 4.0) : 0;
        double bpmDetected = 60.0 * frameRate / bestLag;
        double phase = TrackBeatPhase(odf, bestLag, frameRate, bpmDetected);

        return new BpmResult(Math.Round(bpmDetected, 1), Math.Round(confidence, 3), Math.Round(phase, 3));
    }

    // ── Onset envelope (streaming, ring-buffered) ─────────────────────────────
    private static List<double> ComputeOnsetEnvelope(ISampleProvider source, int channels, double frameRate)
    {
        int m = (int)Math.Log2(FftSize);
        var window = Hann(FftSize);
        var bandEdges = LogBandEdges(FftSize / 2, Bands);

        var ring = new float[FftSize];
        int pos = 0, filled = 0, hopCount = 0;

        var fft = new Complex[FftSize];
        var prevMag = new float[FftSize / 2];
        bool havePrev = false;
        var odf = new List<double>();
        long maxFrames = (long)(MaxSeconds * frameRate);

        var read = new float[8192 * channels];
        int got;
        while (odf.Count < maxFrames && (got = source.Read(read, 0, read.Length)) > 0)
        {
            int frames = got / channels;
            for (int f = 0; f < frames; f++)
            {
                float mono = 0;
                int bi = f * channels;
                for (int c = 0; c < channels; c++) mono += read[bi + c];
                mono /= channels;

                ring[pos] = mono;
                pos = (pos + 1) % FftSize;
                if (filled < FftSize) filled++;
                hopCount++;

                if (filled == FftSize && hopCount >= Hop)
                {
                    hopCount = 0;
                    for (int i = 0; i < FftSize; i++)
                    {
                        fft[i].X = (float)(ring[(pos + i) % FftSize] * window[i]);
                        fft[i].Y = 0f;
                    }
                    FastFourierTransform.FFT(true, m, fft);

                    double flux = 0;
                    for (int b = 0; b < Bands; b++)
                    {
                        int lo = bandEdges[b], hi = bandEdges[b + 1];
                        for (int k = lo; k < hi; k++)
                        {
                            float mag = MathF.Sqrt(fft[k].X * fft[k].X + fft[k].Y * fft[k].Y);
                            if (havePrev) { float d = mag - prevMag[k]; if (d > 0) flux += d; }
                            prevMag[k] = mag;
                        }
                    }
                    havePrev = true;
                    odf.Add(flux);
                    if (odf.Count >= maxFrames) break;
                }
            }
        }
        return odf;
    }

    // Subtract a trailing moving average and half-wave rectify to sharpen onsets.
    private static void Normalize(List<double> odf)
    {
        const int w = 16;
        int n = odf.Count;
        var sm = new double[n];
        double sum = 0; int count = 0;
        for (int i = 0; i < n; i++)
        {
            sum += odf[i]; count++;
            if (count > w) { sum -= odf[i - w]; count--; }
            sm[i] = sum / count;
        }
        for (int i = 0; i < n; i++) { double v = odf[i] - sm[i]; odf[i] = v > 0 ? v : 0; }
    }

    // Ellis DP beat tracker; returns the beat-grid phase (seconds into a beat).
    private static double TrackBeatPhase(List<double> o, double periodFrames, double frameRate, double bpm)
    {
        int n = o.Count;
        if (n < periodFrames * 2) return 0;
        const double tightness = 100.0;

        var score = new double[n];
        var back = new int[n];
        for (int t = 0; t < n; t++)
        {
            int lo = (int)(t - 2 * periodFrames);
            int hi = (int)(t - 0.5 * periodFrames);
            if (lo < 0) lo = 0;

            double best = double.NegativeInfinity; int bestTau = -1;
            for (int tau = lo; tau <= hi && tau < t; tau++)
            {
                if (tau < 0) continue;
                double d = Math.Log((t - tau) / periodFrames);
                double s = score[tau] - tightness * d * d;
                if (s > best) { best = s; bestTau = tau; }
            }
            if (bestTau < 0) { score[t] = o[t]; back[t] = -1; }
            else { score[t] = o[t] + best; back[t] = bestTau; }
        }

        int from = Math.Max(0, (int)(n - periodFrames));
        int endT = from; double bestEnd = double.NegativeInfinity;
        for (int t = from; t < n; t++) if (score[t] > bestEnd) { bestEnd = score[t]; endT = t; }

        int cur = endT, firstBeat = endT;
        while (cur >= 0) { firstBeat = cur; cur = back[cur]; }

        double firstBeatSec = firstBeat / frameRate;
        double beatPeriodSec = 60.0 / bpm;
        double phase = firstBeatSec % beatPeriodSec;
        if (phase < 0) phase += beatPeriodSec;
        return phase;
    }

    private static double TempoPrior(double bpm)
    {
        const double sigma = 0.9; // octaves
        double l = Math.Log2(bpm / 120.0);
        return Math.Exp(-0.5 * (l * l) / (sigma * sigma));
    }

    private static double[] Hann(int n)
    {
        var w = new double[n];
        for (int i = 0; i < n; i++) w[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (n - 1)));
        return w;
    }

    private static int[] LogBandEdges(int maxBin, int bands)
    {
        var e = new int[bands + 1];
        double lo = Math.Log(1), hi = Math.Log(maxBin);
        for (int i = 0; i <= bands; i++) e[i] = (int)Math.Round(Math.Exp(lo + (hi - lo) * i / bands));
        e[0] = 1; e[bands] = maxBin;
        for (int i = 1; i <= bands; i++) if (e[i] <= e[i - 1]) e[i] = e[i - 1] + 1;
        return e;
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    /// <summary>
    /// Detects tempo on a bounded window of a file: seeks to <paramref name="startSec"/>
    /// and reads at most <paramref name="lengthSec"/> seconds (or to EOF). Used by the
    /// live mix-point check to detect the tempo actually playing at the crossfade,
    /// independent of the stored whole-track value. Returns null on any failure.
    /// </summary>
    public BpmResult? AnalyzeFileWindow(string path, double startSec, double lengthSec)
    {
        try
        {
            using var r = new SafeAudioFileReader(path);
            if (startSec > 0)
            {
                try { r.CurrentTime = TimeSpan.FromSeconds(startSec); } catch { }
            }
            return Analyze(new WindowSampleProvider(r, lengthSec));
        }
        catch { return null; }
    }

    // Passes through at most a fixed number of seconds from an inner provider,
    // then reports EOF — so Analyse sees a bounded window rather than reading to
    // its own 120 s cap.
    private sealed class WindowSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private long _remaining; // interleaved float samples

        public WindowSampleProvider(ISampleProvider src, double seconds)
        {
            _src = src;
            _remaining = (long)(seconds * src.WaveFormat.SampleRate * src.WaveFormat.Channels);
            if (_remaining < 0) _remaining = 0;
        }

        public WaveFormat WaveFormat => _src.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            int toRead = count < _remaining ? count : (int)_remaining;
            int got = _src.Read(buffer, offset, toRead);
            _remaining -= got;
            return got;
        }
    }
}
