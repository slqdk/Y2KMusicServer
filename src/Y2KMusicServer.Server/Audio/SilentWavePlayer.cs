// ═══════════════════════════════════════════════════════════════════════════
//  SilentWavePlayer — drives the NAudio pipeline at real-time speed without a
//  sound card. Tight Stopwatch loop with timeBeginPeriod(1) for 1 ms timer
//  resolution, avoiding the 10–15 ms Thread.Sleep drift that causes gaps in
//  the stream. In service mode this is the ONLY output device — there is no
//  WaveOutEvent path (the service runs as LocalSystem with no audio session).
//  Ported unchanged from the legacy build.
// ═══════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Y2KMusicServer.Server.Audio;

public sealed class SilentWavePlayer : IWavePlayer
{
    [DllImport("winmm.dll")] private static extern int timeBeginPeriod(int uPeriod);
    [DllImport("winmm.dll")] private static extern int timeEndPeriod(int uPeriod);

    public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;
    public float Volume { get; set; } = 1f;
    public WaveFormat? OutputWaveFormat => _provider?.WaveFormat;
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    private IWaveProvider? _provider;
    private Thread? _thread;
    private volatile bool _running;

    public void Init(IWaveProvider waveProvider)
    {
        _provider = waveProvider ?? throw new ArgumentNullException(nameof(waveProvider));
    }

    public void Play()
    {
        if (PlaybackState == PlaybackState.Playing) return;
        if (_provider == null) throw new InvalidOperationException("Call Init() before Play().");

        // Resume an existing (paused) pump rather than starting a second
        // thread. The legacy build leaned on WaveOutEvent for pause/resume;
        // in service mode SilentWavePlayer is the only device, so it must
        // resume correctly on its own.
        if (PlaybackState == PlaybackState.Paused && _running)
        {
            PlaybackState = PlaybackState.Playing;
            return;
        }

        PlaybackState = PlaybackState.Playing;
        _running = true;
        _thread = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = "SilentWavePlayer",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    public void Pause()
    {
        if (PlaybackState == PlaybackState.Playing)
            PlaybackState = PlaybackState.Paused;
    }

    public void Stop()
    {
        _running = false;
        PlaybackState = PlaybackState.Stopped;
    }

    private void PumpLoop()
    {
        timeBeginPeriod(1);
        try
        {
            var fmt = _provider!.WaveFormat;
            int bytesPerMs = Math.Max(1, fmt.AverageBytesPerSecond / 1000);

            // 20 ms buffer aligned to block boundary
            int bufBytes = bytesPerMs * 20;
            bufBytes -= bufBytes % Math.Max(1, fmt.BlockAlign);
            var buffer = new byte[bufBytes];

            var sw = Stopwatch.StartNew();
            long targetUs = 0; // microsecond-precision target

            while (_running)
            {
                if (PlaybackState == PlaybackState.Paused)
                {
                    Thread.Sleep(20);
                    sw.Restart();
                    targetUs = 0;
                    continue;
                }

                int read = _provider.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    PlaybackState = PlaybackState.Stopped;
                    PlaybackStopped?.Invoke(this, new StoppedEventArgs());
                    return;
                }

                // Advance target by exact duration of this buffer
                double readMs = read * 1000.0 / fmt.AverageBytesPerSecond;
                targetUs += (long)(readMs * 1000.0);

                long nowUs = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
                long sleepUs = targetUs - nowUs;

                if (sleepUs > 2000)
                    Thread.Sleep((int)((sleepUs - 1000) / 1000));

                // Spin for the final sub-millisecond
                while (true)
                {
                    nowUs = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
                    if (nowUs >= targetUs) break;
                    Thread.SpinWait(10);
                }
            }
        }
        catch (Exception ex)
        {
            PlaybackStopped?.Invoke(this, new StoppedEventArgs(ex));
        }
        finally
        {
            timeEndPeriod(1);
            PlaybackState = PlaybackState.Stopped;
        }
    }

    public void Dispose()
    {
        _running = false;
        PlaybackState = PlaybackState.Stopped;
        _thread?.Join(1000);
    }
}
