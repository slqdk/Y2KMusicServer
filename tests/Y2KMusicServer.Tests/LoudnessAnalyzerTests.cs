using NAudio.Wave;
using Xunit;
using Y2KMusicServer.Server.Audio;

namespace Y2KMusicServer.Tests;

public class LoudnessAnalyzerTests
{
    // A finite-length sine generator (interleaved, IEEE float). amp 0 = silence.
    private sealed class ToneProvider : ISampleProvider
    {
        private readonly double _inc;
        private readonly float _amp;
        private readonly int _channels;
        private long _framesLeft;
        private double _phase;

        public ToneProvider(double freq, float amp, int sampleRate, int channels, double seconds)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _inc = 2.0 * Math.PI * freq / sampleRate;
            _amp = amp;
            _channels = channels;
            _framesLeft = (long)(sampleRate * seconds);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int framesWanted = count / _channels;
            int frames = (int)Math.Min(framesWanted, _framesLeft);
            int n = 0;
            for (int f = 0; f < frames; f++)
            {
                float s = (float)(_amp * Math.Sin(_phase));
                _phase += _inc;
                for (int c = 0; c < _channels; c++) buffer[offset + n++] = s;
            }
            _framesLeft -= frames;
            return n;
        }
    }

    private static double Measure(double freq, float amp, int channels, double seconds, int fs = 48000)
        => new LoudnessAnalyzer().MeasureIntegrated(new ToneProvider(freq, amp, fs, channels, seconds))
           ?? double.NegativeInfinity;

    [Fact]
    public void Silence_returns_null()
    {
        var r = new LoudnessAnalyzer().MeasureIntegrated(new ToneProvider(1000, 0f, 48000, 2, 2.0));
        Assert.Null(r);
    }

    [Fact]
    public void Clip_shorter_than_one_block_returns_null()
    {
        var r = new LoudnessAnalyzer().MeasureIntegrated(new ToneProvider(1000, 0.5f, 48000, 2, 0.2));
        Assert.Null(r);
    }

    [Fact]
    public void Full_scale_1k_stereo_sine_is_near_zero_lufs()
    {
        // Unweighted a full-scale stereo sine sits at ~−0.7 LUFS; K-weighting at
        // 1 kHz nudges it slightly. A generous range guards the math without
        // pinning exact coefficients.
        double lufs = Measure(1000, 1.0f, channels: 2, seconds: 3.0);
        Assert.InRange(lufs, -4.0, 1.0);
    }

    [Fact]
    public void Twenty_dB_quieter_reads_about_twenty_lu_lower()
    {
        double loud = Measure(1000, 0.5f, 2, 3.0);
        double quiet = Measure(1000, 0.05f, 2, 3.0); // −20 dB
        Assert.InRange(loud - quiet, 18.0, 22.0);
    }

    [Fact]
    public void Mono_is_about_three_lu_below_stereo_for_same_tone()
    {
        double stereo = Measure(1000, 0.5f, 2, 3.0);
        double mono = Measure(1000, 0.5f, 1, 3.0);
        Assert.InRange(stereo - mono, 2.0, 4.0); // 10*log10(2) ≈ 3.01
    }

    [Fact]
    public void Works_at_44100_too()
    {
        double a = Measure(1000, 0.5f, 2, 3.0, fs: 48000);
        double b = Measure(1000, 0.5f, 2, 3.0, fs: 44100);
        Assert.InRange(Math.Abs(a - b), 0.0, 1.0); // rate-independent within ~1 LU
    }
}
