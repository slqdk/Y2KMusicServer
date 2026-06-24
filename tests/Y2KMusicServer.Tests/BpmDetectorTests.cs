using NAudio.Wave;
using Xunit;
using Y2KMusicServer.Server.Audio;

namespace Y2KMusicServer.Tests;

public class BpmDetectorTests
{
    // A click train: a short percussive burst every (60/bpm) seconds, otherwise
    // silence. Strong broadband onsets — the easy, unambiguous tempo case.
    private sealed class ClickTrain : ISampleProvider
    {
        private readonly int _period;     // frames between clicks
        private readonly int _burst;      // frames of "click"
        private readonly int _offset;     // frames before the first click
        private readonly int _channels;
        private long _framesLeft;
        private long _t;

        public ClickTrain(double bpm, int sampleRate, int channels, double seconds, double offsetSec = 0)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _period = (int)Math.Round(sampleRate * 60.0 / bpm);
            _burst = Math.Max(1, sampleRate / 500); // ~2 ms
            _offset = (int)Math.Round(sampleRate * offsetSec);
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
                long idx = _t - _offset;
                bool click = idx >= 0 && (idx % _period) < _burst;
                float s = click ? 0.9f : 0f;
                for (int c = 0; c < _channels; c++) buffer[offset + n++] = s;
                _t++;
            }
            _framesLeft -= frames;
            return n;
        }
    }

    private static BpmResult? Detect(double bpm, double seconds = 12.0, int fs = 44100, double offsetSec = 0)
        => new BpmDetector().Analyze(new ClickTrain(bpm, fs, 2, seconds, offsetSec));

    [Theory]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(128)]
    [InlineData(140)]
    public void Detects_click_train_tempo(double bpm)
    {
        var r = Detect(bpm);
        Assert.NotNull(r);
        Assert.InRange(r!.Bpm, bpm - 3.0, bpm + 3.0);
        Assert.True(r.Confidence > 0.1, $"confidence too low: {r.Confidence}");
    }

    [Fact]
    public void Silence_returns_null()
    {
        var r = new BpmDetector().Analyze(new ToneSilence(44100, 2, 12.0));
        Assert.Null(r);
    }

    [Fact]
    public void Phase_is_within_one_beat()
    {
        var r = Detect(120, offsetSec: 0.0);
        Assert.NotNull(r);
        double beat = 60.0 / r!.Bpm;
        Assert.InRange(r.BeatPhaseOffsetSec, 0.0, beat);
    }

    // Pure silence provider.
    private sealed class ToneSilence : ISampleProvider
    {
        private long _left;
        private readonly int _ch;
        public ToneSilence(int fs, int ch, double sec) { WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(fs, ch); _ch = ch; _left = (long)(fs * sec); }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            int frames = (int)Math.Min(count / _ch, _left);
            int n = frames * _ch;
            for (int i = 0; i < n; i++) buffer[offset + i] = 0f;
            _left -= frames;
            return n;
        }
    }
}
