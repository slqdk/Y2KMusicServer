using NAudio.Wave;

namespace Y2KMusicServer.Server.Audio;

/// <summary>
/// EBU R128 / ITU-R BS.1770 integrated loudness, in LUFS. The signal is
/// K-weighted (a high-shelf "pre-filter" then an RLB high-pass, both derived
/// for the actual sample rate), measured in 400 ms blocks at 100 ms hop, and
/// gated absolutely (−70 LUFS) then relatively (−10 LU). The result is what the
/// Phase 5 analysis pass writes to <c>Track.LufsIntegrated</c>; the audio
/// engine's <c>NormalizedVolume</c> already reads it.
///
/// <see cref="MeasureIntegrated"/> takes an <see cref="ISampleProvider"/> so it
/// can be unit-tested against synthetic PCM without touching the filesystem or
/// Media Foundation; <see cref="AnalyzeFile"/> wraps a real file.
/// </summary>
public sealed class LoudnessAnalyzer
{
    private const double AbsoluteGateLufs = -70.0;
    private const double RelativeGateLu = -10.0;
    private const double Offset = -0.691; // BS.1770 loudness offset

    /// <summary>Integrated LUFS, or null for silence / clips shorter than 400 ms / errors.</summary>
    public double? AnalyzeFile(string path)
    {
        try
        {
            using var reader = new SafeAudioFileReader(path);
            return MeasureIntegrated(reader);
        }
        catch
        {
            return null;
        }
    }

    public double? MeasureIntegrated(ISampleProvider source)
    {
        int fs = source.WaveFormat.SampleRate;
        int channels = source.WaveFormat.Channels;
        if (fs <= 0 || channels <= 0) return null;

        int ch = Math.Min(channels, 2); // mono or stereo; ignore extra channels
        int subLen = (int)Math.Round(fs * 0.1); // 100 ms sub-block
        if (subLen < 1) return null;

        var shelf = Biquad.HighShelf(fs);
        var hp = Biquad.HighPass(fs);

        // Per-channel filter state for each of the two stages.
        var z1a = new double[ch]; var z2a = new double[ch];
        var z1b = new double[ch]; var z2b = new double[ch];

        var sumSq = new double[ch];
        int subCount = 0;

        // Ring of the last four sub-block mean-squares (per channel).
        var ring = new double[4][];
        for (int i = 0; i < 4; i++) ring[i] = new double[ch];
        int ringFilled = 0, ringHead = 0;

        var blocks = new List<(double l, double z)>();

        var buf = new float[8192 * channels];
        int read;
        while ((read = source.Read(buf, 0, buf.Length)) > 0)
        {
            int frames = read / channels;
            for (int f = 0; f < frames; f++)
            {
                int baseIdx = f * channels;
                for (int c = 0; c < ch; c++)
                {
                    double x = buf[baseIdx + c];
                    double a = shelf.Process(x, ref z1a[c], ref z2a[c]);
                    double y = hp.Process(a, ref z1b[c], ref z2b[c]);
                    sumSq[c] += y * y;
                }
                if (++subCount >= subLen)
                {
                    var slot = ring[ringHead];
                    for (int c = 0; c < ch; c++) { slot[c] = sumSq[c] / subLen; sumSq[c] = 0; }
                    ringHead = (ringHead + 1) % 4;
                    if (ringFilled < 4) ringFilled++;
                    subCount = 0;

                    if (ringFilled == 4)
                    {
                        double z = 0;
                        for (int c = 0; c < ch; c++)
                        {
                            double avg = (ring[0][c] + ring[1][c] + ring[2][c] + ring[3][c]) / 4.0;
                            z += avg; // channel weight 1.0 for L/R (and mono)
                        }
                        if (z > 0)
                        {
                            double l = Offset + 10.0 * Math.Log10(z);
                            blocks.Add((l, z));
                        }
                    }
                }
            }
        }

        if (blocks.Count == 0) return null;

        // Absolute gate.
        double absSum = 0; int absN = 0;
        foreach (var b in blocks)
            if (b.l >= AbsoluteGateLufs) { absSum += b.z; absN++; }
        if (absN == 0) return null;

        // Relative gate at (mean loudness of absolute-passing blocks) − 10 LU.
        double relGate = Offset + 10.0 * Math.Log10(absSum / absN) + RelativeGateLu;
        double relSum = 0; int relN = 0;
        foreach (var b in blocks)
            if (b.l >= AbsoluteGateLufs && b.l >= relGate) { relSum += b.z; relN++; }
        if (relN == 0) return null;

        return Offset + 10.0 * Math.Log10(relSum / relN);
    }

    /// <summary>A normalised biquad (a0 = 1), transposed Direct Form II.</summary>
    private readonly struct Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;

        private Biquad(double b0, double b1, double b2, double a1, double a2)
        { _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2; }

        public double Process(double x, ref double z1, ref double z2)
        {
            double y = _b0 * x + z1;
            z1 = _b1 * x - _a1 * y + z2;
            z2 = _b2 * x - _a2 * y;
            return y;
        }

        // K-weighting stage 1: high-shelf. Analog parameters per BS.1770
        // (these reproduce the published 48 kHz reference coefficients).
        public static Biquad HighShelf(int fs)
        {
            const double f0 = 1681.974450955533;
            const double q = 0.7071752369554196;
            const double gainDb = 3.999843853973347;

            double a = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2.0 * Math.PI * f0 / fs;
            double cos = Math.Cos(w0), sin = Math.Sin(w0);
            double alpha = sin / (2.0 * q);
            double sqrtA2alpha = 2.0 * Math.Sqrt(a) * alpha;

            double b0 = a * ((a + 1) + (a - 1) * cos + sqrtA2alpha);
            double b1 = -2.0 * a * ((a - 1) + (a + 1) * cos);
            double b2 = a * ((a + 1) + (a - 1) * cos - sqrtA2alpha);
            double a0 = (a + 1) - (a - 1) * cos + sqrtA2alpha;
            double a1 = 2.0 * ((a - 1) - (a + 1) * cos);
            double a2 = (a + 1) - (a - 1) * cos - sqrtA2alpha;

            return new Biquad(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
        }

        // K-weighting stage 2: RLB high-pass.
        public static Biquad HighPass(int fs)
        {
            const double f0 = 38.13547087613982;
            const double q = 0.5003270373238773;

            double w0 = 2.0 * Math.PI * f0 / fs;
            double cos = Math.Cos(w0), sin = Math.Sin(w0);
            double alpha = sin / (2.0 * q);

            double b0 = (1 + cos) / 2.0;
            double b1 = -(1 + cos);
            double b2 = (1 + cos) / 2.0;
            double a0 = 1 + alpha;
            double a1 = -2.0 * cos;
            double a2 = 1 - alpha;

            return new Biquad(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
        }
    }
}
