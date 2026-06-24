// ═══════════════════════════════════════════════════════════════════════════
//  IsoFilter — per-deck EQ "isolator", DJ-mixer style. This is NOT stem
//  separation; it is a steep band filter, the same trick a hardware mixer's
//  "Bass / Isolate" does. Pass-through ISampleProvider inserted AFTER the VU
//  meter and BEFORE the volume fader, so the soundcard and the /stream tap
//  (both downstream of the fader, via DeckTap) hear its output while the FFT
//  kick detector and the VU upstream keep seeing the deck's full-band content.
//  Four modes:
//    None   — true bypass, zero cost.
//    Bass   — low-pass cascade (~130 Hz, ~48 dB/oct) per channel. Steep on
//             purpose: the kick + bassline survive, the midrange falls off a
//             cliff so the tune stops being recognisable.
//    NoBass — high-pass cascade at the SAME corner: the complement of Bass.
//             Kills the kick/bassline and keeps everything above. The deck for
//             a bass swap — overlap a NoBass deck with a full deck and only one
//             low end is in the mix, so the basslines don't clash.
//    Vocal  — centre-extract (L+R)/2, then a steep high-pass (~200 Hz) to gut
//             the kick/bass and a gentle low-pass (~6 kHz) to drop hiss, written
//             to every channel. Foregrounds a centre-mixed lead vocal; it does
//             NOT remove instruments sharing that band and it collapses to mono.
//             An approximation, not isolation — true vocal stems are a later
//             (offline analyze-pass) feature.
//
//  Thread model: the audio (pump) thread calls Read; the control thread calls
//  SetMode. A mode change builds a brand-new immutable Stage and publishes it
//  by a single volatile reference assignment. Only the audio thread mutates the
//  BiQuad state, and only ever on the *current* Stage, so there is no
//  cross-thread contention and no lock on the hot path. Rebuilding the filters
//  on every change also means re-engaging a mode never clicks on stale state.
// ═══════════════════════════════════════════════════════════════════════════

using NAudio.Dsp;
using NAudio.Wave;

namespace Y2KMusicServer.Server.Audio;

public enum IsoMode { None, Bass, Vocal, NoBass }

public sealed class IsoFilter : ISampleProvider
{
    // Bass / NoBass share this corner so NoBass is the true complement of Bass.
    private const float BassCutoffHz = 130f;
    private const int BandSections = 4;       // ~48 dB/oct, low- or high-pass.

    // Vocal band — steep high-pass (drop kick/bass) + gentle low-pass (keep
    // clarity, lose only the very top hiss/air).
    private const float VocalHpHz = 200f;
    private const int VocalHpSections = 4;    // ~48 dB/oct
    private const float VocalLpHz = 6000f;

    private const float Q = 0.7071f;          // Butterworth (maximally flat) sections.

    private readonly ISampleProvider _source;
    private readonly int _sampleRate;
    private readonly int _channels;

    private volatile Stage _stage;

    public IsoFilter(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _sampleRate = source.WaveFormat.SampleRate;
        _channels = source.WaveFormat.Channels;
        _stage = new Stage(IsoMode.None, _sampleRate, _channels);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>The active mode. Read freely from any thread.</summary>
    public IsoMode Mode => _stage.Mode;

    /// <summary>Switch the active mode. A single reference assignment — the audio
    /// thread picks up the new Stage on its next Read. No-op if unchanged.</summary>
    public void SetMode(IsoMode mode)
    {
        if (mode == _stage.Mode) return;
        _stage = new Stage(mode, _sampleRate, _channels);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read <= 0) return read;

        var s = _stage; // single volatile read; this whole block sees one Stage
        if (s.Mode == IsoMode.None) return read;

        int ch = _channels;
        int frames = read / ch;

        if (s.Mode == IsoMode.Bass || s.Mode == IsoMode.NoBass)
        {
            // Per-channel cascade — low-pass for Bass, high-pass for NoBass.
            for (int f = 0; f < frames; f++)
            {
                int b = offset + f * ch;
                for (int c = 0; c < ch; c++)
                {
                    float x = buffer[b + c];
                    var sections = s.Cascade[c];
                    for (int i = 0; i < sections.Length; i++) x = sections[i].Transform(x);
                    buffer[b + c] = x;
                }
            }
        }
        else // Vocal
        {
            for (int f = 0; f < frames; f++)
            {
                int b = offset + f * ch;
                float mono = 0f;
                for (int c = 0; c < ch; c++) mono += buffer[b + c];
                mono /= ch;
                for (int i = 0; i < s.VHp.Length; i++) mono = s.VHp[i].Transform(mono);
                mono = s.VLp.Transform(mono);
                for (int c = 0; c < ch; c++) buffer[b + c] = mono;
            }
        }
        return read;
    }

    /// <summary>Immutable per-mode filter set. Constructed fresh on every mode
    /// change; the audio thread is the only mutator of the BiQuad state and only
    /// touches the current Stage, so no locking is needed on Read.</summary>
    private sealed class Stage
    {
        public readonly IsoMode Mode;

        // Bass / NoBass: one low-pass (Bass) or high-pass (NoBass) cascade per
        // channel ([channel][section]).
        public readonly BiQuadFilter[][] Cascade = Array.Empty<BiQuadFilter[]>();

        // Vocal mode: a single mono band — high-pass cascade + one low-pass.
        public readonly BiQuadFilter[] VHp = Array.Empty<BiQuadFilter>();
        public readonly BiQuadFilter VLp = null!;

        public Stage(IsoMode mode, int sampleRate, int channels)
        {
            Mode = mode;
            if (mode == IsoMode.Bass || mode == IsoMode.NoBass)
            {
                bool lowPass = mode == IsoMode.Bass;
                Cascade = new BiQuadFilter[channels][];
                for (int c = 0; c < channels; c++)
                {
                    Cascade[c] = new BiQuadFilter[BandSections];
                    for (int i = 0; i < BandSections; i++)
                        Cascade[c][i] = lowPass
                            ? BiQuadFilter.LowPassFilter(sampleRate, BassCutoffHz, Q)
                            : BiQuadFilter.HighPassFilter(sampleRate, BassCutoffHz, Q);
                }
            }
            else if (mode == IsoMode.Vocal)
            {
                VHp = new BiQuadFilter[VocalHpSections];
                for (int i = 0; i < VocalHpSections; i++)
                    VHp[i] = BiQuadFilter.HighPassFilter(sampleRate, VocalHpHz, Q);
                VLp = BiQuadFilter.LowPassFilter(sampleRate, VocalLpHz, Q);
            }
            // None: no filters; Read bypasses before touching any of these.
        }
    }
}
