namespace Y2KMusicServer.Server.Data.Entities;

/// <summary>
/// Single-row table of user-mutable settings (the admin page edits these
/// live). Operational config — Kestrel port, log path, GitHub coordinates,
/// the install-time default LUFS — lives in appsettings.json, not here.
/// Seeded defaults match the legacy WinForms app.
/// </summary>
public sealed class Settings
{
    /// <summary>Always 1 — this table holds a single row.</summary>
    public int Id { get; set; }

    public bool SmartMix { get; set; }
    public bool SmartBeatFader { get; set; }
    public int NextTriggerPct { get; set; }
    public int NextFadeSeconds { get; set; }
    public bool AutoDj { get; set; }
    public int AutoDjTracks { get; set; }
    public int AutoDjBpmDev { get; set; }
    public int ScanWorkers { get; set; }
    public bool NormalizeEnabled { get; set; }
    public bool LimiterEnabled { get; set; }
    public double TargetLufs { get; set; }
    public int Volume { get; set; }
    /// <summary>Stream bitrate in kbps. Legacy persisted a dropdown index (0=64,
    /// 1=128, 2=192, 3=320); this stores the resolved kbps value directly.</summary>
    public int StreamingBitrate { get; set; }

    public bool AllowWebNext { get; set; }
    public bool ShowWebCategories { get; set; }
    public bool DebugLogging { get; set; }

    /// <summary>Legacy free-form update URL. Superseded by the GitHub
    /// coordinates in appsettings.json; kept for import fidelity.</summary>
    public string? UpdateUrl { get; set; }
}
