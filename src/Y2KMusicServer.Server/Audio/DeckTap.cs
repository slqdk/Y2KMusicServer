// ═══════════════════════════════════════════════════════════════════════════
//  DeckTap — an ISampleProvider pass-through inserted into each deck's chain.
//  The deck's SilentWavePlayer pump pulls samples through it at real-time
//  speed; as they flow past they are copied into a private per-deck ring.
//  The streaming distribute thread later drains that ring and mixes the two
//  decks. Two DeckTaps never share a ring, so the pump (writer) and the
//  distribute thread (reader) only ever contend on one deck's lock — never
//  across decks.
//
//  The tap captures POST-volume, POST-normalisation samples: it sits after
//  the deck's VolumeSampleProvider (so the crossfade ramp is reflected) and
//  after the channel/sample-rate normaliser (so every deck feeds the stream
//  at the same fixed format regardless of the source file). Ported from the
//  legacy build's StreamingEncoder.DeckTap, de-WinForms'd.
// ═══════════════════════════════════════════════════════════════════════════

using NAudio.Wave;

namespace Y2KMusicServer.Server.Audio;

public sealed class DeckTap : ISampleProvider
{
    private readonly ISampleProvider _source;

    // ~8 s of stereo float at 44100 Hz — generous headroom so a briefly
    // stalled distribute thread doesn't lose audio.
    private const int RingCap = 44100 * 2 * 8;
    private readonly float[] _ring = new float[RingCap];
    private int _writePos;
    private int _readPos;
    private int _available;
    private readonly object _lock = new();

    public WaveFormat WaveFormat => _source.WaveFormat;

    public DeckTap(ISampleProvider source)
        => _source = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>
    /// Called ONLY by the deck's pump thread. Pulls from upstream and copies the
    /// captured samples into the ring. If the ring is full (distribute thread
    /// fell behind) the oldest samples are overwritten — the live signal always
    /// wins over stale audio.
    /// </summary>
    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read > 0)
        {
            lock (_lock)
            {
                for (int i = 0; i < read; i++)
                {
                    _ring[_writePos] = buffer[offset + i];
                    _writePos = (_writePos + 1) % RingCap;
                    if (_available < RingCap) _available++;
                    else _readPos = (_readPos + 1) % RingCap;
                }
            }
        }
        return read;
    }

    /// <summary>
    /// Called ONLY by the distribute thread. Drains up to <paramref name="count"/>
    /// samples; returns however many were actually available (may be fewer).
    /// </summary>
    public int Drain(float[] buf, int count)
    {
        lock (_lock)
        {
            int n = Math.Min(_available, count);
            for (int i = 0; i < n; i++)
            {
                buf[i] = _ring[_readPos];
                _readPos = (_readPos + 1) % RingCap;
            }
            _available -= n;
            return n;
        }
    }

    public int Available { get { lock (_lock) return _available; } }

    /// <summary>Drop everything buffered so the next read returns only samples
    /// written after this call. Used when a pre-rolled deck enters the mix, to
    /// skip the backlog accumulated while it ran silently.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _writePos = 0;
            _readPos = 0;
            _available = 0;
        }
    }
}
