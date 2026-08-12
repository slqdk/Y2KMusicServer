// ═══════════════════════════════════════════════════════════════════════════
//  AudioHealthMonitor — per-deck audio-content diagnostics + black-box capture.
//
//  Motivation: intermittent "static" during mixing sections / hard cuts, heard
//  on BOTH the /stream broadcast and the local sound card. The two paths share
//  everything upstream of the DeckTap, so the corruption enters the deck chain
//  itself. Three candidate mechanisms with distinct fingerprints:
//    • decode / I-O stalls  → real-time underrun → zero-gaps      ("dropout static")
//    • non-finite samples   → filter/decode emitting NaN/Inf      ("harsh noise")
//    • hard discontinuities → clicks at cuts / seeks / vol steps  ("crackle")
//
//  A HealthTap is a pass-through ISampleProvider inserted POST-fader, just
//  before the DeckTap — so it inspects exactly the signal both the local
//  output and the stream receive. It never modifies samples. Per read it
//  scans for the fingerprints above, aggregates into ~1 s windows, and fires
//  a callback when a window closes anomalous; the engine logs the window at
//  Warning with full playback context (track, position, crossfade / plan /
//  isolator state). Clean audio logs nothing.
//
//  Every HealthTap also keeps a rolling capture ring (last ~10 s of float
//  audio). The StreamingEncoder registers a ring of the post-mix (pre-clip)
//  signal too. AudioBlackBox dumps all registered rings to WAV files under
//  <DataPath>\diagnostics\ — on demand (tray / POST endpoint) or automatically
//  when a deck window closes anomalous while DebugLogging is on (cooldown-
//  limited). Listening to deck-A / deck-B / mix captures of the same burst
//  pins which stage made the noise.
// ═══════════════════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.Wave;

namespace Y2KMusicServer.Server.Audio;

/// <summary>Summary of one closed health window (~1 s of pulled audio).</summary>
public sealed class HealthWindow
{
    public int Reads { get; init; }
    public long Samples { get; init; }

    /// <summary>NaN/Inf samples seen. Any occurrence = a filter or decode
    /// stage emitted invalid audio.</summary>
    public int NonFinite { get; init; }

    /// <summary>Sample-to-sample jumps beyond the click threshold.</summary>
    public int Clicks { get; init; }

    /// <summary>Largest sample-to-sample jump in the window (finite samples).</summary>
    public float MaxDelta { get; init; }

    /// <summary>Longest run of exact-zero samples (ms) after signal had been
    /// seen — silence injected mid-signal.</summary>
    public double MaxZeroRunMs { get; init; }

    /// <summary>Reads whose wall-clock cost exceeded the real-time budget of
    /// the audio they returned — decode / share-I/O stalls, the upstream cause
    /// of both local underruns and stream-ring starvation.</summary>
    public int SlowReads { get; init; }

    /// <summary>Most expensive single read (ms).</summary>
    public double MaxReadMs { get; init; }

    /// <summary>Reads that returned fewer samples than asked (EOF gives one).</summary>
    public int Shortfalls { get; init; }

    public float Peak { get; init; }

    public bool Anomalous =>
        NonFinite > 0 || Clicks > 0 || SlowReads > 0 || MaxZeroRunMs >= 50.0;
}

/// <summary>
/// Pass-through sample provider that inspects (never alters) the audio and
/// records it into a rolling capture ring. Single-reader: only the deck's
/// output pump calls <see cref="Read"/>. The window callback fires on that
/// pump thread — handlers must be quick and must not take the engine gate.
/// </summary>
public sealed class HealthTap : ISampleProvider
{
    /// <summary>Sample-to-sample jump treated as a click. Music at unity
    /// rarely exceeds ~0.5 between consecutive samples at 44.1 kHz; a hard
    /// cut or corrupt buffer easily does.</summary>
    private const float ClickThreshold = 0.9f;

    private const long WindowMicros = 1_000_000;

    private readonly ISampleProvider _source;
    private readonly Action<HealthTap, HealthWindow> _onAnomalousWindow;

    /// <summary>Diagnostic name ("deckA"/"deckB"); the engine updates it on
    /// promotion so dumps and logs carry the current role.</summary>
    public string Name { get; set; }

    public CaptureRing Ring { get; }

    public WaveFormat WaveFormat => _source.WaveFormat;

    // Window accumulators — touched only on the pump thread.
    private long _winStartMicros = -1;
    private int _reads, _nonFinite, _clicks, _slowReads, _shortfalls;
    private long _samples;
    private float _maxDelta, _peak;
    private long _maxReadMicros;
    private long _maxZeroRun, _curZeroRun;
    private float _prevSample;
    private bool _seenSignal;

    public HealthTap(ISampleProvider source, string name, int captureSeconds,
                     Action<HealthTap, HealthWindow> onAnomalousWindow)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _onAnomalousWindow = onAnomalousWindow ?? throw new ArgumentNullException(nameof(onAnomalousWindow));
        Name = name;
        Ring = new CaptureRing(captureSeconds, source.WaveFormat.SampleRate, source.WaveFormat.Channels);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        long t0 = Stopwatch.GetTimestamp();
        int read = _source.Read(buffer, offset, count);
        long elapsedMicros = (Stopwatch.GetTimestamp() - t0) * 1_000_000L / Stopwatch.Frequency;
        long nowMicros = Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency;

        if (read > 0)
        {
            _reads++;
            _samples += read;
            if (read < count) _shortfalls++;
            if (elapsedMicros > _maxReadMicros) _maxReadMicros = elapsedMicros;

            var wf = _source.WaveFormat;
            long budgetMicros = (long)(read / (double)wf.Channels / wf.SampleRate * 1_000_000.0);
            // Ignore micro-reads where scheduler jitter dominates the budget.
            if (elapsedMicros > budgetMicros && elapsedMicros > 5000) _slowReads++;

            float prev = _prevSample;
            for (int i = 0; i < read; i++)
            {
                float v = buffer[offset + i];
                if (!float.IsFinite(v)) { _nonFinite++; prev = 0f; continue; }

                float av = Math.Abs(v);
                if (av > _peak) _peak = av;

                float d = Math.Abs(v - prev);
                if (d > _maxDelta) _maxDelta = d;
                if (d > ClickThreshold) _clicks++;
                prev = v;

                if (v == 0f)
                {
                    if (_seenSignal && ++_curZeroRun > _maxZeroRun) _maxZeroRun = _curZeroRun;
                }
                else
                {
                    _seenSignal = true;
                    _curZeroRun = 0;
                }
            }
            _prevSample = prev;

            Ring.Write(buffer, offset, read);
        }
        else
        {
            _shortfalls++;
        }

        // Window roll.
        if (_winStartMicros < 0) _winStartMicros = nowMicros;
        if (nowMicros - _winStartMicros >= WindowMicros)
        {
            var wf = _source.WaveFormat;
            var win = new HealthWindow
            {
                Reads = _reads,
                Samples = _samples,
                NonFinite = _nonFinite,
                Clicks = _clicks,
                MaxDelta = _maxDelta,
                MaxZeroRunMs = _maxZeroRun / (double)wf.Channels / wf.SampleRate * 1000.0,
                SlowReads = _slowReads,
                MaxReadMs = _maxReadMicros / 1000.0,
                Shortfalls = _shortfalls,
                Peak = _peak
            };

            _winStartMicros = nowMicros;
            _reads = 0; _samples = 0; _nonFinite = 0; _clicks = 0; _slowReads = 0;
            _shortfalls = 0; _maxDelta = 0f; _peak = 0f; _maxReadMicros = 0; _maxZeroRun = 0;

            if (win.Anomalous)
            {
                try { _onAnomalousWindow(this, win); } catch { /* diagnostics never break audio */ }
            }
        }

        return read;
    }
}

/// <summary>
/// Rolling float capture ring (single writer — the pump thread; occasional
/// snapshot readers). Dumpable as a 32-bit-float WAV.
/// </summary>
public sealed class CaptureRing
{
    private readonly float[] _buf;
    private readonly object _gate = new();
    private int _write;
    private bool _wrapped;

    public int SampleRate { get; }
    public int Channels { get; }

    public CaptureRing(int seconds, int sampleRate, int channels)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _buf = new float[Math.Max(1, seconds) * sampleRate * channels];
    }

    public void Write(float[] src, int offset, int count)
    {
        lock (_gate)
        {
            for (int i = 0; i < count; i++)
            {
                _buf[_write] = src[offset + i];
                if (++_write == _buf.Length) { _write = 0; _wrapped = true; }
            }
        }
    }

    /// <summary>Oldest-first linearised copy of the captured audio.</summary>
    public float[] Snapshot()
    {
        lock (_gate)
        {
            if (!_wrapped)
            {
                var head = new float[_write];
                Array.Copy(_buf, 0, head, 0, _write);
                return head;
            }
            var full = new float[_buf.Length];
            int tail = _buf.Length - _write;
            Array.Copy(_buf, _write, full, 0, tail);
            Array.Copy(_buf, 0, full, tail, _write);
            return full;
        }
    }

    /// <summary>Writes the ring to <paramref name="path"/> as float WAV.
    /// Returns false when the ring is still empty.</summary>
    public bool DumpWav(string path)
    {
        var data = Snapshot();
        if (data.Length == 0) return false;
        using var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels));
        w.WriteSamples(data, 0, data.Length);
        return true;
    }
}

/// <summary>
/// Registry of live capture rings (deck taps + the encoder's post-mix ring)
/// and the dump-to-disk logic. Static because producers live in different
/// subsystems (engine decks come and go; the encoder is a singleton) and the
/// dump must snapshot all of them at one instant.
/// </summary>
public static class AudioBlackBox
{
    private sealed class Entry
    {
        public required Func<string> Name { get; init; }
        public required CaptureRing Ring { get; init; }
    }

    private static readonly ConcurrentDictionary<object, Entry> Entries = new();
    private static long _lastAutoDumpTicks;

    /// <summary>Auto-dump at most once per this interval.</summary>
    private static readonly TimeSpan AutoDumpCooldown = TimeSpan.FromSeconds(30);

    /// <summary>Registers a ring; dispose the token to unregister (deck teardown).</summary>
    public static IDisposable Register(Func<string> name, CaptureRing ring)
    {
        var key = new object();
        Entries[key] = new Entry { Name = name, Ring = ring };
        return new Registration(key);
    }

    private sealed class Registration : IDisposable
    {
        private readonly object _key;
        public Registration(object key) => _key = key;
        public void Dispose() => Entries.TryRemove(_key, out _);
    }

    /// <summary>Dumps every registered ring to <paramref name="dir"/>. Returns
    /// the files written (empty rings are skipped).</summary>
    public static List<string> DumpAll(string dir)
    {
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var files = new List<string>();
        foreach (var e in Entries.Values)
        {
            string name = "ring";
            try { name = e.Name(); } catch { }
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');

            var path = Path.Combine(dir, $"blackbox-{stamp}-{name}.wav");
            // Two decks can briefly share a role name mid-promotion; keep both.
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(dir, $"blackbox-{stamp}-{name}-{i}.wav");

            try { if (e.Ring.DumpWav(path)) files.Add(path); }
            catch { /* a failed ring must not abort the others */ }
        }
        return files;
    }

    /// <summary>Cooldown-limited automatic dump (anomaly-triggered).</summary>
    public static List<string>? TryAutoDump(string dir)
    {
        long now = DateTime.UtcNow.Ticks;
        long last = Interlocked.Read(ref _lastAutoDumpTicks);
        if (now - last < AutoDumpCooldown.Ticks) return null;
        if (Interlocked.CompareExchange(ref _lastAutoDumpTicks, now, last) != last) return null;
        return DumpAll(dir);
    }
}
