// ═══════════════════════════════════════════════════════════════════════════
//  SafeAudioFileReader — drop-in for AudioFileReader that avoids the mfds.dll
//  access violation on certain FLAC files. For .flac it uses
//  MediaFoundationReader directly and converts to float; for all other formats
//  it delegates to AudioFileReader. No extra NuGet packages — FLAC support
//  rides on Windows Media Foundation. Ported unchanged from the legacy build.
// ═══════════════════════════════════════════════════════════════════════════

using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Y2KMusicServer.Server.Audio;

/// <summary>
/// Wraps <see cref="MediaFoundationReader"/> (for .flac) or
/// <see cref="AudioFileReader"/> (all other formats) behind a unified
/// WaveStream + ISampleProvider + Volume surface.
/// </summary>
public sealed class SafeAudioFileReader : WaveStream, ISampleProvider
{
    private readonly WaveStream _inner;        // MFR or AudioFileReader
    private readonly ISampleProvider _samples; // float sample view
    private float _volume = 1f;

    public SafeAudioFileReader(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ext == ".flac")
        {
            // Use MediaFoundationReader DIRECTLY — avoids the SampleChannel
            // layer inside AudioFileReader that triggers the mfds.dll crash.
            var mfr = new MediaFoundationReader(filePath);
            _inner = mfr;
            _samples = mfr.ToSampleProvider();
        }
        else
        {
            var afr = new AudioFileReader(filePath);
            _inner = afr;
            _samples = afr;
        }
    }

    // Report the float sample provider's WaveFormat, not the raw inner reader's.
    // For FLAC, _inner is PCM but _samples has converted to IeeeFloat; reporting
    // PCM here makes MeteringSampleProvider and others reject the chain.
    public override WaveFormat WaveFormat => _samples.WaveFormat;

    private long ScaleToFloat(long innerBytes)
    {
        if (_inner.WaveFormat.AverageBytesPerSecond == 0) return innerBytes;
        double secs = innerBytes / (double)_inner.WaveFormat.AverageBytesPerSecond;
        return (long)(secs * _samples.WaveFormat.AverageBytesPerSecond);
    }

    private long ScaleToInner(long floatBytes)
    {
        if (_samples.WaveFormat.AverageBytesPerSecond == 0) return floatBytes;
        double secs = floatBytes / (double)_samples.WaveFormat.AverageBytesPerSecond;
        return (long)(secs * _inner.WaveFormat.AverageBytesPerSecond);
    }

    public override long Length => ScaleToFloat(_inner.Length);

    public override long Position
    {
        get => ScaleToFloat(_inner.Position);
        set
        {
            long innerPos = ScaleToInner(value);
            int align = _inner.WaveFormat.BlockAlign;
            if (align > 0) innerPos = (innerPos / align) * align;
            _inner.Position = Math.Min(innerPos, _inner.Length);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
        => _inner.Read(buffer, offset, count);

    WaveFormat ISampleProvider.WaveFormat => _samples.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _samples.Read(buffer, offset, count);

        if (_volume != 1f)
            for (int i = offset; i < offset + read; i++)
                buffer[i] *= _volume;

        return read;
    }

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Max(0f, value);
    }

    public ISampleProvider ToSampleProvider() => this;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner?.Dispose();
        base.Dispose(disposing);
    }
}
