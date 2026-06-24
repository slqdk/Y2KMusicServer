using System.Text.Json;
using Y2KMusicServer.Server.Data;

namespace Y2KMusicServer.Server.Audio;

/// <summary>
/// Cached waveform data for one track: a min/max envelope of the audio,
/// quantized to signed bytes. The beat grid (bpm / phase) is NOT stored here —
/// it lives on the Track row and is mutable — so editing the grid never forces
/// the waveform to be recomputed. <see cref="SamplesPerPoint"/> and
/// <see cref="SampleRate"/> let a renderer map a point index to a timestamp:
/// <c>time = index * SamplesPerPoint / SampleRate</c>.
/// </summary>
public sealed class WaveformData
{
    /// <summary>Audio frames summarised by each min/max pair.</summary>
    public int SamplesPerPoint { get; init; }

    public int SampleRate { get; init; }

    /// <summary>Track length derived from the samples actually decoded.</summary>
    public double DurationSec { get; init; }

    /// <summary>
    /// Interleaved <c>min, max, min, max, …</c> per window, each in -127..127
    /// (the audio sample range -1..1 scaled by 127). Two entries per window.
    /// </summary>
    public sbyte[] Peaks { get; init; } = Array.Empty<sbyte>();
}

/// <summary>
/// Computes a compact min/max waveform envelope for a track and caches it on
/// disk (<c>data\peaks\&lt;trackId&gt;.json</c>). Computation is lazy: the
/// waveform endpoint builds it on the first request for a track and reads the
/// cache thereafter. The envelope is decoded through <see cref="SafeAudioFileReader"/>,
/// the same path the engine and analysers use (so FLAC works via Media Foundation).
/// </summary>
public static class WaveformPeaks
{
    // Target number of min/max windows for a whole track. Caps the payload and
    // keeps resolution roughly constant regardless of length; the per-window
    // frame count is sized up for longer tracks so the count stays near this.
    private const int TargetWindows = 8000;

    // Floor on frames-per-window, so very short tracks still aggregate a little
    // rather than emitting a point per handful of samples.
    private const int MinSamplesPerPoint = 256;

    /// <summary>
    /// Returns the cached waveform for a track, computing and caching it on a
    /// miss. Throws if the file cannot be opened/decoded (caller maps to 404).
    /// </summary>
    public static WaveformData GetOrBuild(IConfiguration cfg, int trackId, string filePath)
    {
        var dir = DataPaths.EnsurePeaksDir(cfg);
        var file = Path.Combine(dir, trackId + ".json");

        if (File.Exists(file))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<WaveformData>(File.ReadAllText(file));
                if (cached != null && cached.Peaks.Length > 0) return cached;
            }
            catch
            {
                // Corrupt/old cache file — fall through and recompute.
            }
        }

        var data = Compute(filePath);

        try { File.WriteAllText(file, JsonSerializer.Serialize(data)); }
        catch { /* cache write is best-effort; serving still works */ }

        return data;
    }

    /// <summary>
    /// Decodes <paramref name="filePath"/> to a mono min/max envelope. Channels
    /// are averaged per frame. Throws on an unreadable file.
    /// </summary>
    public static WaveformData Compute(string filePath)
    {
        using var reader = new SafeAudioFileReader(filePath);

        int channels = reader.WaveFormat.Channels;
        int sampleRate = reader.WaveFormat.SampleRate;
        if (channels <= 0 || sampleRate <= 0)
        {
            return new WaveformData
            {
                SamplesPerPoint = MinSamplesPerPoint,
                SampleRate = sampleRate > 0 ? sampleRate : 1,
                DurationSec = 0,
                Peaks = Array.Empty<sbyte>()
            };
        }

        // Size the window from an estimate of total frames so the whole track
        // lands in ~TargetWindows points. Length is float-sample bytes.
        long totalFrames = reader.Length / sizeof(float) / channels;
        int spp = (int)Math.Max(MinSamplesPerPoint, Math.Ceiling(totalFrames / (double)TargetWindows));
        if (spp < 1) spp = MinSamplesPerPoint;

        var peaks = new List<sbyte>(TargetWindows * 2 + 64);
        var buf = new float[8192 * channels];

        float wMin = float.MaxValue, wMax = float.MinValue;
        int frameInWindow = 0;
        long framesRead = 0;
        int read;

        while ((read = reader.Read(buf, 0, buf.Length)) > 0)
        {
            int frames = read / channels;
            for (int f = 0; f < frames; f++)
            {
                int baseIdx = f * channels;
                float mono = 0f;
                for (int c = 0; c < channels; c++) mono += buf[baseIdx + c];
                mono /= channels;

                if (mono < wMin) wMin = mono;
                if (mono > wMax) wMax = mono;

                if (++frameInWindow >= spp)
                {
                    peaks.Add(Q(wMin));
                    peaks.Add(Q(wMax));
                    wMin = float.MaxValue;
                    wMax = float.MinValue;
                    frameInWindow = 0;
                }
            }
            framesRead += frames;
        }

        // Flush a trailing partial window.
        if (frameInWindow > 0)
        {
            peaks.Add(Q(wMin));
            peaks.Add(Q(wMax));
        }

        return new WaveformData
        {
            SamplesPerPoint = spp,
            SampleRate = sampleRate,
            DurationSec = framesRead / (double)sampleRate,
            Peaks = peaks.ToArray()
        };

        static sbyte Q(float v)
        {
            int q = (int)MathF.Round(Math.Clamp(v, -1f, 1f) * 127f);
            return (sbyte)Math.Clamp(q, -127, 127);
        }
    }
}
