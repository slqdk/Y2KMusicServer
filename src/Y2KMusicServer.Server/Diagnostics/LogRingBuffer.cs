using Serilog.Events;

namespace Y2KMusicServer.Server.Diagnostics;

/// <summary>
/// Bounded in-memory ring of the most recent log entries. <see cref="RingBufferSink"/>
/// feeds it; <c>AdminLogsController</c> reads snapshots; <c>LogHubBroadcaster</c>
/// subscribes to <see cref="Emitted"/> and pushes each line over SignalR.
///
/// Singleton, thread-safe, and independent of the daily file sink — clearing or
/// losing this buffer never touches the on-disk log. The buffer only ever holds
/// the last <see cref="Capacity"/> lines, so it is memory-bounded regardless of
/// how long the service runs.
/// </summary>
public sealed class LogRingBuffer
{
    public const int Capacity = 2000;

    private readonly object _gate = new();
    private readonly LogEntryDto[] _ring = new LogEntryDto[Capacity];
    private int _count;
    private int _head; // next write index
    private long _seq;

    /// <summary>Raised after each entry is stored. Handlers must not throw.</summary>
    public event Action<LogEntryDto>? Emitted;

    public void Add(DateTime tsUtc, string level, string source, string message, string? exception)
    {
        LogEntryDto entry;
        lock (_gate)
        {
            entry = new LogEntryDto
            {
                Seq = ++_seq,
                TimestampUtc = tsUtc,
                Level = level,
                Source = source,
                Message = message,
                Exception = exception
            };
            _ring[_head] = entry;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }

        // Fire outside the lock so a slow or broken subscriber can never stall
        // the logging path, and swallow anything it throws.
        try { Emitted?.Invoke(entry); }
        catch { /* a log subscriber must not break logging */ }
    }

    /// <summary>
    /// The most recent entries, oldest first, filtered to >= <paramref name="minLevel"/>
    /// and capped to the last <paramref name="take"/>.
    /// </summary>
    public IReadOnlyList<LogEntryDto> Snapshot(int take, LogEventLevel minLevel = LogEventLevel.Verbose)
    {
        if (take <= 0) take = 200;
        lock (_gate)
        {
            var ordered = new List<LogEntryDto>(_count);
            int start = (_head - _count + Capacity) % Capacity;
            for (int i = 0; i < _count; i++)
            {
                var e = _ring[(start + i) % Capacity];
                if (e is not null && LevelAtLeast(e.Level, minLevel)) ordered.Add(e);
            }
            if (ordered.Count > take)
                ordered.RemoveRange(0, ordered.Count - take);
            return ordered;
        }
    }

    private static bool LevelAtLeast(string level, LogEventLevel min)
        => Enum.TryParse<LogEventLevel>(level, out var l) && l >= min;
}
