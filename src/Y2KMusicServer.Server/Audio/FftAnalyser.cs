// ═══════════════════════════════════════════════════════════════════════════
//  FftAnalyser — kick-drum detector: FFT sub-bass band (40–100 Hz) + two-phase
//  onset detection. PRIMED when bassRms spikes above the slow floor; FIRING
//  confirms on the next window once the transient has peaked, then emits
//  BassOnset and enters a refractory period. Pass-through ISampleProvider —
//  it taps the stream without altering it. BassOnset is volatile, safe to read
//  from any thread. Ported unchanged from the legacy build.
// ═══════════════════════════════════════════════════════════════════════════

using NAudio.Dsp;
using NAudio.Wave;

namespace Y2KMusicServer.Server.Audio;

public sealed class FftAnalyser : ISampleProvider
{
    private const float BassLowHz = 40f;
    private const float BassHighHz = 100f;

    private const float SlowDecay = 0.92f;
    private const float FastDecay = 0.30f;
    private const float RiseRatio = 1.8f;
    private const int RefractoryWindows = 6;
    private const int FftSize = 1024; // ~23 ms at 44100 Hz

    private readonly ISampleProvider _source;
    private readonly int _sampleRate;
    private readonly int _channels;

    private readonly float[] _window = new float[FftSize];
    private readonly Complex[] _fftBuf = new Complex[FftSize];

    private int _winPos;
    private float _slowAvg;
    private float _fastAvg;
    private float _prevFast;

    private bool _primed;
    private float _primedStr;
    private int _refractory;

    /// <summary>Beat strength on a confirmed kick frame (0.1–1.0), 0 otherwise.
    /// Volatile — read freely from any thread.</summary>
    public volatile float BassOnset;

    public FftAnalyser(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _sampleRate = source.WaveFormat.SampleRate;
        _channels = source.WaveFormat.Channels;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read <= 0) return read;

        int frames = read / _channels;
        for (int f = 0; f < frames; f++)
        {
            float mono = 0f;
            for (int c = 0; c < _channels; c++)
                mono += buffer[offset + f * _channels + c];
            mono /= _channels;

            float hann = 0.5f * (1f - (float)Math.Cos(2.0 * Math.PI * _winPos / (FftSize - 1)));
            _window[_winPos++] = mono * hann;

            if (_winPos >= FftSize)
            {
                RunFft();
                _winPos = 0;
            }
        }
        return read;
    }

    private void RunFft()
    {
        for (int i = 0; i < FftSize; i++)
        {
            _fftBuf[i].X = _window[i];
            _fftBuf[i].Y = 0f;
        }
        FastFourierTransform.FFT(true, (int)Math.Log(FftSize, 2), _fftBuf);

        float freqPerBin = _sampleRate / (float)FftSize;
        int binLow = Math.Max(1, (int)Math.Ceiling(BassLowHz / freqPerBin));
        int binHigh = Math.Min(FftSize / 2, (int)Math.Floor(BassHighHz / freqPerBin));

        float energy = 0f;
        for (int b = binLow; b <= binHigh; b++)
        {
            float re = _fftBuf[b].X, im = _fftBuf[b].Y;
            energy += re * re + im * im;
        }
        float bassRms = (float)Math.Sqrt(energy / Math.Max(1, binHigh - binLow + 1));

        if (_refractory > 0) _refractory--;

        _prevFast = _fastAvg;
        _slowAvg = _slowAvg * SlowDecay + bassRms * (1f - SlowDecay);
        _fastAvg = _fastAvg * FastDecay + bassRms * (1f - FastDecay);

        if (_refractory == 0)
        {
            if (_primed)
            {
                if (_fastAvg <= _prevFast * 1.05f)
                {
                    BassOnset = _primedStr;
                    _refractory = RefractoryWindows;
                }
                else
                {
                    _primedStr = Math.Max(_primedStr,
                        Math.Min(1f, (bassRms / (_slowAvg * RiseRatio) - 1f) * 0.6f + 0.3f));
                    BassOnset = 0f;
                }
                _primed = false;
            }
            else
            {
                BassOnset = 0f;
            }

            if (_slowAvg > 1e-7f && bassRms > _slowAvg * RiseRatio && !_primed)
            {
                _primed = true;
                _primedStr = Math.Min(1f, (bassRms / (_slowAvg * RiseRatio) - 1f) * 0.6f + 0.3f);
                _primedStr = Math.Max(0.1f, _primedStr);
            }
        }
        else
        {
            _primed = false;
            BassOnset = 0f;
        }
    }
}
