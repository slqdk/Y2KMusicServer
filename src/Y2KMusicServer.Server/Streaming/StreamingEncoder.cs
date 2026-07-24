// ═══════════════════════════════════════════════════════════════════════════
//  StreamingEncoder — the live broadcast.
//
//  Architecture
//  ────────────
//  • The two decks each own a DeckTap in their chain (see AudioEngine). The
//    engine raises TapsChanged whenever the live deck set changes; this encoder
//    stores the current A/B taps under _tapLock.
//
//  • A single distribute thread wakes every 20 ms (deadline-paced, matching the
//    SilentWavePlayer pump size exactly), drains whatever both taps have, mixes
//    + soft-clips ONCE, and converts to PCM16. From that one mixed buffer it
//    branches by listener format:
//      – WAV listeners receive the raw PCM16 chunk.
//      – MP3 listeners receive frames from ONE shared LAME encoder (not one per
//        listener). A new MP3 listener simply joins the frame stream in flight,
//        exactly like internet radio. LAME runs only while ≥1 MP3 listener is
//        connected; with WAV-only listeners it never spins up.
//
//  • Per-listener delivery is non-blocking: each StreamListener owns a bounded
//    channel that drops the oldest chunk when full, so one slow browser can
//    never stall the distribute thread (the "refresh fixes it" / backed-up send
//    path failure mode the project guards against).
//
//  Fixed stream format: 44100 Hz, 2 ch. Every deck is normalised to this ahead
//  of its tap, so the WAV header and the LAME input format are constant no
//  matter what sample rate / channel count the source files have.
// ═══════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using NAudio.Lame;
using NAudio.Wave;
using Y2KMusicServer.Server.Audio;
using Y2KMusicServer.Server.Data;
using Y2KMusicServer.Server.Data.Entities;

namespace Y2KMusicServer.Server.Streaming;

public enum StreamFormat { Wav, Mp3 }

public sealed record StreamStatus
{
    public int Bitrate { get; init; }
    public int Listeners { get; init; }
    public int WavListeners { get; init; }
    public int Mp3Listeners { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────────
//  StreamListener — one per connected HTTP client.
// ─────────────────────────────────────────────────────────────────────────────
public sealed class StreamListener : IDisposable
{
    // ~5 s of 20 ms chunks. DropOldest gives the legacy "discard oldest when
    // full" behaviour with an async reader, so the distribute thread's TryWrite
    // never blocks.
    private readonly Channel<byte[]> _channel = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(250)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    public StreamFormat Format { get; }
    public bool IsDisposed { get; private set; }

    public StreamListener(StreamFormat format) => Format = format;

    internal void Enqueue(byte[] chunk)
    {
        if (IsDisposed) return;
        _channel.Writer.TryWrite(chunk); // DropOldest handles a full queue
    }

    /// <summary>Awaits the next chunk; returns null when cancelled or closed.</summary>
    public async Task<byte[]?> TakeNextAsync(CancellationToken ct)
    {
        try { return await _channel.Reader.ReadAsync(ct); }
        catch (OperationCanceledException) { return null; }
        catch (ChannelClosedException) { return null; }
    }

    public void Dispose()
    {
        IsDisposed = true;
        _channel.Writer.TryComplete();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  StreamingEncoder
// ─────────────────────────────────────────────────────────────────────────────
public sealed class StreamingEncoder : IHostedService, IDisposable
{
    public const int SampleRate = 44100;
    public const int Channels = 2;

    [DllImport("winmm.dll")] private static extern int timeBeginPeriod(int uPeriod);
    [DllImport("winmm.dll")] private static extern int timeEndPeriod(int uPeriod);

    private readonly AudioEngine _engine;
    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly ILogger<StreamingEncoder> _log;

    private volatile int _bitrate = 128;

    private readonly object _tapLock = new();
    private DeckTap? _tapA;
    private DeckTap? _tapB;

    private readonly List<StreamListener> _listeners = new();
    private readonly ReaderWriterLockSlim _listenerLock = new();

    private Thread? _distThread;
    private CancellationTokenSource? _cts;

    // Owned exclusively by the distribute thread — no locking needed.
    private LameMP3FileWriter? _lame;
    private MemoryStream? _mp3Capture;
    private int _lameBitrate;

    public StreamingEncoder(
        AudioEngine engine,
        IDbContextFactory<Y2KDbContext> dbf,
        ILogger<StreamingEncoder> log)
    {
        _engine = engine;
        _dbf = dbf;
        _log = log;
    }

    public int Bitrate => _bitrate;

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Seed enabled/bitrate from the persisted Settings row.
        try
        {
            await using var db = await _dbf.CreateDbContextAsync(cancellationToken);
            var s = await db.Settings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
            if (s != null)
            {
                _bitrate = NormaliseBitrate(s.StreamingBitrate);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read streaming settings at startup; defaulting off.");
        }

        _engine.TapsChanged += OnTapsChanged;

        _cts = new CancellationTokenSource();
        _distThread = new Thread(() => DistributeLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "StreamDistribute",
            Priority = ThreadPriority.AboveNormal
        };
        _distThread.Start();

        _log.LogInformation(
            "Streaming encoder started (always on, bitrate={Bitrate} kbps, {Rate} Hz / {Ch} ch).",
            _bitrate, SampleRate, Channels);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _engine.TapsChanged -= OnTapsChanged;
        _cts?.Cancel();
        try { _distThread?.Join(2000); } catch { }
        DisposeAllListeners();
        return Task.CompletedTask;
    }

    private void OnTapsChanged(DeckTap? a, DeckTap? b)
    {
        lock (_tapLock) { _tapA = a; _tapB = b; }
    }

    // ── Public control surface (used by StreamController) ────────────────────────

    public async Task SetBitrateAsync(int kbps, CancellationToken ct = default)
    {
        _bitrate = NormaliseBitrate(kbps);
        await PersistAsync(s => s.StreamingBitrate = _bitrate, ct);
    }

    public StreamStatus GetStatus()
    {
        int wav = 0, mp3 = 0;
        _listenerLock.EnterReadLock();
        try
        {
            foreach (var l in _listeners)
            {
                if (l.IsDisposed) continue;
                if (l.Format == StreamFormat.Mp3) mp3++; else wav++;
            }
        }
        finally { _listenerLock.ExitReadLock(); }

        return new StreamStatus
        {
            Bitrate = _bitrate,
            Listeners = wav + mp3,
            WavListeners = wav,
            Mp3Listeners = mp3,
            SampleRate = SampleRate,
            Channels = Channels
        };
    }

    public StreamListener AddListener(StreamFormat format)
    {
        var l = new StreamListener(format);
        _listenerLock.EnterWriteLock();
        try { _listeners.Add(l); }
        finally { _listenerLock.ExitWriteLock(); }
        _log.LogInformation("Stream listener connected ({Format}).", format);
        return l;
    }

    // 44-byte WAV header with unknown/streaming sizes (0xFFFFFFFF). 16-bit PCM.
    public byte[] BuildWavHeader()
    {
        int byteRate = SampleRate * Channels * 2;
        int blockAlign = Channels * 2;
        var h = new byte[44];
        void Str(int off, string s) => System.Text.Encoding.ASCII.GetBytes(s).CopyTo(h, off);
        void I32(int off, int v) => BitConverter.GetBytes(v).CopyTo(h, off);
        void U32(int off, uint v) => BitConverter.GetBytes(v).CopyTo(h, off);
        void I16(int off, short v) => BitConverter.GetBytes(v).CopyTo(h, off);
        Str(0, "RIFF"); U32(4, 0xFFFFFFFFu);
        Str(8, "WAVE"); Str(12, "fmt ");
        I32(16, 16); I16(20, 1);                 // PCM
        I16(22, (short)Channels);
        I32(24, SampleRate);
        I32(28, byteRate);
        I16(32, (short)blockAlign);
        I16(34, 16);                             // bits per sample
        Str(36, "data"); U32(40, 0xFFFFFFFFu);   // unknown length (streaming)
        return h;
    }

    // ── Distribute thread ────────────────────────────────────────────────────────

    /// <summary>
    /// Soft-clips a sample using a tanh Padé approximation. Unity-gain below
    /// ~0.7; smoothly saturates toward ±1 for hot signals (two decks summed).
    /// </summary>
    private static float SoftClip(float s)
    {
        s *= 0.95f;
        if (s > 3f) return 1f;
        if (s < -3f) return -1f;
        float s2 = s * s;
        return s * (27f + s2) / (27f + 9f * s2);
    }

    // Invariant two-decimal float for log lines (decimal separator "." on any
    // host locale, matching the engine's verbose-log contract).
    private static string Inv(float v) => v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    // Stream-health unit helpers: audio frames → ms, and ring fill (interleaved
    // float samples) → ms. int.MaxValue means "never sampled" → reported as -1.
    private static long FramesToMs(long frames) => frames * 1000 / SampleRate;
    private static int RingMs(int interleavedSamples)
        => interleavedSamples == int.MaxValue ? -1
           : (int)((long)interleavedSamples / Channels * 1000 / SampleRate);

    private void DistributeLoop(CancellationToken ct)
    {
        const int chunkMs = 20;
        int samplesPerChunk = SampleRate * Channels * chunkMs / 1000; // 1764

        var bufA = new float[samplesPerChunk];
        var bufB = new float[samplesPerChunk];
        var mixed = new float[samplesPerChunk];
        var pcm16 = new byte[samplesPerChunk * 2];

        // 1 ms timer resolution so the WaitOne sleeps are accurate; without it a
        // default Windows system can oversleep 30+ ms, skipping cycles → silence.
        timeBeginPeriod(1);
        var sw = Stopwatch.StartNew();
        long targetUs = 0;

        // ── Stream-health instrumentation (DebugLogging only) ─────────────────
        // Aggregated over a ~1 s window and emitted only when a deck actually
        // starved the mix, so a clean stream logs nothing and a glitchy transition
        // logs exactly when, on which deck, and how badly. "Dropouts" are the
        // zero-filled gaps that the ear hears as static; "min ring" is how close the
        // tap came to running dry.
        const long healthWindowUs = 1_000_000;
        long winStartUs = -1;
        long zfFramesA = 0, zfFramesB = 0;
        int shortfallsA = 0, shortfallsB = 0;
        int minRingA = int.MaxValue, minRingB = int.MaxValue;

        // ── Mix-level instrumentation (DebugLogging only) ─────────────────────
        // Aggregated over the same ~1 s window as Stream health. Discriminates
        // where mixing-time "static" enters the chain:
        //   • peak A / B  — per-deck post-fader peak straight off the taps. A
        //     deck whose peak pins ≥ ~1.0 (or wildly above) is delivering hot or
        //     garbage samples BEFORE the mix — points upstream (iso / volume /
        //     decode) on that deck.
        //   • peak sum    — the summed signal BEFORE SoftClip. Sustained > 1 is
        //     the two-decks-hot condition SoftClip then saturates.
        //   • saturated   — ms of the window the pre-clip sum spent above unity;
        //     large values mean the "static" may simply be sustained clip fuzz.
        //   • non-finite  — NaN/Inf samples per deck; any occurrence means a
        //     filter or decode stage is emitting invalid audio.
        // Emitted only when Debug is on AND the window was hot / invalid /
        // starved, so a clean stream still logs nothing.
        float peakA = 0f, peakB = 0f, peakSum = 0f;
        long satFrames = 0;
        int nonFiniteA = 0, nonFiniteB = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // ── Deadline pacer ────────────────────────────────────────────
                targetUs += chunkMs * 1000L;
                long nowUs = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;

                // If stalled for more than two chunks (GC / OS pause / overnight),
                // re-anchor so we don't sprint to catch up — catching up faster
                // than real-time overflows listener queues and clicks.
                const long maxBehindUs = chunkMs * 1000L * 2;
                if (nowUs - targetUs > maxBehindUs) targetUs = nowUs;

                long sleepUs = targetUs - nowUs;
                if (sleepUs > 2000)
                {
                    try { ct.WaitHandle.WaitOne((int)((sleepUs - 1000) / 1000)); }
                    catch (OperationCanceledException) { break; }
                }
                while (!ct.IsCancellationRequested)
                {
                    nowUs = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
                    if (nowUs >= targetUs) break;
                    Thread.SpinWait(10);
                }
                if (ct.IsCancellationRequested) break;

                // ── Listener census ───────────────────────────────────────────
                int mp3Count = 0, totalCount = 0;
                List<StreamListener>? dead = null;
                _listenerLock.EnterReadLock();
                try
                {
                    foreach (var l in _listeners)
                    {
                        if (l.IsDisposed)
                        {
                            (dead ??= new List<StreamListener>()).Add(l);
                            continue;
                        }
                        totalCount++;
                        if (l.Format == StreamFormat.Mp3) mp3Count++;
                    }
                }
                finally { _listenerLock.ExitReadLock(); }
                if (dead != null) PruneListeners(dead);

                if (totalCount == 0) { TearDownLame(); continue; }

                // ── Snapshot taps + drain ─────────────────────────────────────
                DeckTap? a, b;
                lock (_tapLock) { a = _tapA; b = _tapB; }

                int readA = a?.Drain(bufA, samplesPerChunk) ?? 0;
                int readB = b?.Drain(bufB, samplesPerChunk) ?? 0;

                // Stream-health sampling: a live tap that returns less than a full
                // chunk forces a zero-filled (silent) gap below; record it and how
                // close the ring is to empty. Cheap — one DeckTap lock per live deck.
                if (a != null)
                {
                    if (readA < samplesPerChunk) { shortfallsA++; zfFramesA += (samplesPerChunk - readA) / Channels; }
                    int av = a.Available; if (av < minRingA) minRingA = av;
                }
                if (b != null)
                {
                    if (readB < samplesPerChunk) { shortfallsB++; zfFramesB += (samplesPerChunk - readB) / Channels; }
                    int av = b.Available; if (av < minRingB) minRingB = av;
                }

                // ── Mix-level sampling (Debug only; see window declarations) ──
                if (_log.IsEnabled(LogLevel.Debug))
                {
                    for (int i = 0; i < readA; i++)
                    {
                        float v = bufA[i];
                        if (!float.IsFinite(v)) { nonFiniteA++; continue; }
                        float av = Math.Abs(v);
                        if (av > peakA) peakA = av;
                    }
                    for (int i = 0; i < readB; i++)
                    {
                        float v = bufB[i];
                        if (!float.IsFinite(v)) { nonFiniteB++; continue; }
                        float av = Math.Abs(v);
                        if (av > peakB) peakB = av;
                    }
                    int lim = Math.Max(readA, readB);
                    int satSamples = 0;
                    for (int i = 0; i < lim; i++)
                    {
                        float s = (i < readA ? bufA[i] : 0f) + (i < readB ? bufB[i] : 0f);
                        if (!float.IsFinite(s)) continue;
                        float abs = Math.Abs(s);
                        if (abs > peakSum) peakSum = abs;
                        if (abs > 1f) satSamples++;
                    }
                    satFrames += satSamples / Channels;
                }

                // ── Mix + soft-clip (silence kept flowing for buffer health) ──
                int n = samplesPerChunk;
                if (readA == 0 && readB == 0)
                {
                    Array.Clear(mixed, 0, n);
                }
                else if (readB == 0)
                {
                    for (int i = 0; i < n; i++) mixed[i] = i < readA ? SoftClip(bufA[i]) : 0f;
                }
                else if (readA == 0)
                {
                    for (int i = 0; i < n; i++) mixed[i] = i < readB ? SoftClip(bufB[i]) : 0f;
                }
                else
                {
                    int limit = Math.Max(readA, readB);
                    for (int i = 0; i < limit; i++)
                    {
                        float s = (i < readA ? bufA[i] : 0f) + (i < readB ? bufB[i] : 0f);
                        mixed[i] = SoftClip(s);
                    }
                    if (limit < n) Array.Clear(mixed, limit, n - limit);
                }

                // ── Float → PCM16 LE ──────────────────────────────────────────
                for (int i = 0; i < n; i++)
                {
                    short s16 = (short)(Math.Clamp(mixed[i], -1f, 1f) * 32767f);
                    pcm16[i * 2] = (byte)(s16 & 0xFF);
                    pcm16[i * 2 + 1] = (byte)((s16 >> 8) & 0xFF);
                }

                // ── WAV chunk (always) + MP3 chunk (only if an MP3 listener) ──
                byte[] wavChunk = (byte[])pcm16.Clone();

                byte[]? mp3Chunk = null;
                if (mp3Count > 0)
                {
                    EnsureLame();
                    try
                    {
                        _lame!.Write(pcm16, 0, pcm16.Length);
                        if (_mp3Capture!.Length > 0)
                        {
                            mp3Chunk = _mp3Capture.ToArray();
                            _mp3Capture.SetLength(0);
                            _mp3Capture.Position = 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "MP3 encode failed; re-initialising LAME.");
                        TearDownLame();
                    }
                }
                else
                {
                    TearDownLame();
                }

                // ── Fan out by format — never blocks ──────────────────────────
                List<StreamListener>? died = null;
                _listenerLock.EnterReadLock();
                try
                {
                    foreach (var l in _listeners)
                    {
                        if (l.IsDisposed)
                        {
                            (died ??= new List<StreamListener>()).Add(l);
                            continue;
                        }
                        if (l.Format == StreamFormat.Mp3)
                        {
                            if (mp3Chunk != null) l.Enqueue(mp3Chunk);
                        }
                        else
                        {
                            l.Enqueue(wavChunk);
                        }
                    }
                }
                finally { _listenerLock.ExitReadLock(); }
                if (died != null) PruneListeners(died);

                // ── Stream-health emit: at most once per window, only when Debug is
                // on AND a deck actually starved the mix (otherwise silent) ────────
                if (winStartUs < 0) winStartUs = nowUs;
                else if (nowUs - winStartUs >= healthWindowUs)
                {
                    if (_log.IsEnabled(LogLevel.Debug) && (zfFramesA > 0 || zfFramesB > 0))
                    {
                        _log.LogDebug(
                            "Stream health ({Win}ms): A dropouts={ZfA}ms/{SfA} chunks (min ring {MinA}ms) | B dropouts={ZfB}ms/{SfB} chunks (min ring {MinB}ms)",
                            (nowUs - winStartUs) / 1000,
                            FramesToMs(zfFramesA), shortfallsA, RingMs(minRingA),
                            FramesToMs(zfFramesB), shortfallsB, RingMs(minRingB));
                    }

                    // Mix levels: only when the window was hot (near/above unity
                    // sum), invalid (NaN/Inf), or already starved — a clean
                    // window logs nothing. Floats are pre-formatted invariantly
                    // so the decimal separator is "." on any host locale.
                    if (_log.IsEnabled(LogLevel.Debug)
                        && (nonFiniteA > 0 || nonFiniteB > 0 || peakSum > 0.98f
                            || zfFramesA > 0 || zfFramesB > 0))
                    {
                        _log.LogDebug(
                            "Mix levels ({Win}ms): peak A={PeakA} B={PeakB} sum={PeakSum} | saturated {Sat}ms | non-finite A={NfA} B={NfB}",
                            (nowUs - winStartUs) / 1000,
                            Inv(peakA), Inv(peakB), Inv(peakSum),
                            FramesToMs(satFrames), nonFiniteA, nonFiniteB);
                    }

                    winStartUs = nowUs;
                    zfFramesA = zfFramesB = 0;
                    shortfallsA = shortfallsB = 0;
                    minRingA = minRingB = int.MaxValue;
                    peakA = peakB = peakSum = 0f;
                    satFrames = 0;
                    nonFiniteA = nonFiniteB = 0;
                }
            }
        }
        finally
        {
            timeEndPeriod(1);
            TearDownLame();
        }
    }

    // ── LAME helpers (distribute thread only) ────────────────────────────────────

    private void EnsureLame()
    {
        if (_lame != null && _lameBitrate == _bitrate) return;

        TearDownLame();
        _mp3Capture = new MemoryStream();
        var fmt = new WaveFormat(SampleRate, 16, Channels);
        _lame = new LameMP3FileWriter(_mp3Capture, fmt, _bitrate);
        _lameBitrate = _bitrate;
        _log.LogInformation("LAME MP3 encoder initialised at {Bitrate} kbps.", _bitrate);
    }

    private void TearDownLame()
    {
        if (_lame == null && _mp3Capture == null) return;
        try { _lame?.Dispose(); } catch { }   // flushes final frames into capture (discarded)
        try { _mp3Capture?.Dispose(); } catch { }
        _lame = null;
        _mp3Capture = null;
        _lameBitrate = 0;
    }

    // ── Listener bookkeeping ─────────────────────────────────────────────────────

    private void PruneListeners(List<StreamListener> dead)
    {
        _listenerLock.EnterWriteLock();
        try { foreach (var d in dead) _listeners.Remove(d); }
        finally { _listenerLock.ExitWriteLock(); }
    }

    private void DisposeAllListeners()
    {
        _listenerLock.EnterWriteLock();
        try
        {
            foreach (var l in _listeners) { try { l.Dispose(); } catch { } }
            _listeners.Clear();
        }
        finally { _listenerLock.ExitWriteLock(); }
    }

    // ── Settings persistence ─────────────────────────────────────────────────────

    private async Task PersistAsync(Action<Settings> mutate, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbf.CreateDbContextAsync(ct);
            var s = await db.Settings.FirstOrDefaultAsync(ct);
            if (s == null) return;
            mutate(s);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to persist streaming settings.");
        }
    }

    private static int NormaliseBitrate(int kbps)
        => kbps switch
        {
            <= 64 => 64,
            <= 128 => 128,
            <= 192 => 192,
            _ => 320
        };

    public void Dispose()
    {
        _cts?.Cancel();
        try { _distThread?.Join(2000); } catch { }
        _cts?.Dispose();
        DisposeAllListeners();
        _listenerLock.Dispose();
    }
}
