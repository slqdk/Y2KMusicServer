using Serilog.Core;
using Serilog.Events;

namespace Y2KMusicServer.Server.Diagnostics;

/// <summary>
/// Wraps the Serilog <see cref="LoggingLevelSwitch"/> that governs the whole
/// pipeline's minimum level at runtime. The "verbose logging" preference (the
/// <c>Settings.DebugLogging</c> flag) flips it: on => Debug, so the granular
/// engine / Auto DJ-selection traces become visible; off => Information, the
/// normal activity stream (track starts, crossfades, Auto DJ fills, warnings,
/// errors). Bound into the pipeline via <c>MinimumLevel.ControlledBy</c> in
/// Program.cs; framework namespaces are pinned to Warning separately so they
/// stay quiet even when verbose is on.
/// </summary>
public sealed class LogVerbositySwitch
{
    public LoggingLevelSwitch Switch { get; } = new(LogEventLevel.Information);

    public void SetDebug(bool on)
        => Switch.MinimumLevel = on ? LogEventLevel.Debug : LogEventLevel.Information;

    public string CurrentLevel => Switch.MinimumLevel.ToString();

    public bool DebugEnabled => Switch.MinimumLevel <= LogEventLevel.Debug;
}
