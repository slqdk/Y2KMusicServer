using System.Text.Json;
using Y2KMusicServer.Server.Data;

namespace Y2KMusicServer.Server.Audio;

/// <summary>
/// Operator config for the transition planner, persisted as JSON on disk
/// (<c>&lt;DataPath&gt;\mixrules.json</c>) rather than in the database — the
/// no-migrations rule (persistence.md) keeps new settings off the schema.
///
/// Two section toggles drive the planner: <c>CrossfadeAuto</c> lets it auto-pick
/// the best crossfade (Normal / Beatmatching / Beat drop) for the pair, and
/// <c>MixingAuto</c> lets it auto-pick a musical move (vocal-tease / bass-swap /
/// bass-breakdown), which takes priority when one fits. The per-move toggles gate
/// which moves Mixing may use. Defaults: ±5 BPM, 80% Deck B entry level, Crossfade
/// ON (a real crossfade out of the box) and Mixing OFF (opt in to the moves).
/// </summary>
public sealed class MixRules
{
    public bool CrossfadeAuto { get; set; } = true;
    public bool MixingAuto { get; set; } = false;
    public double BpmTolerance { get; set; } = 5.0;
    public bool VocalTease { get; set; } = true;
    public bool BassSwap { get; set; } = true;
    public bool BassBreakdown { get; set; } = true;
    public double DeckBEntryLevel { get; set; } = 0.80;
    public int BassHoldBars { get; set; } = 4;
    public int MaxOverlapBars { get; set; } = 8;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Reads the rules from disk, or returns defaults on a miss / parse
    /// failure. Loaded values are clamped to sane ranges (in case hand-edited).</summary>
    public static MixRules Load(IConfiguration cfg)
    {
        var path = DataPaths.MixRulesPath(cfg);
        try
        {
            if (File.Exists(path))
            {
                var r = JsonSerializer.Deserialize<MixRules>(File.ReadAllText(path));
                if (r != null) return Sanitize(r);
            }
        }
        catch { /* missing / corrupt → defaults */ }
        return new MixRules();
    }

    /// <summary>Clamps and writes the rules to disk (best-effort). Returns the
    /// sanitised value actually stored.</summary>
    public static MixRules Save(IConfiguration cfg, MixRules incoming)
    {
        var r = Sanitize(incoming ?? new MixRules());
        var path = DataPaths.MixRulesPath(cfg);
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(r, Indented));
        }
        catch { /* best-effort; the returned value still reflects the request */ }
        return r;
    }

    private static MixRules Sanitize(MixRules r)
    {
        r.BpmTolerance = Clamp(r.BpmTolerance, 0, 50);
        r.DeckBEntryLevel = Clamp(r.DeckBEntryLevel, 0, 1);
        r.BassHoldBars = (int)Clamp(r.BassHoldBars, 0, 32);
        r.MaxOverlapBars = (int)Clamp(r.MaxOverlapBars, 1, 64);
        return r;
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
}
