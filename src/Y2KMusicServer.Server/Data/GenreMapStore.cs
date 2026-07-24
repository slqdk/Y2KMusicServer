using System.Text.Json;
using Y2KMusicServer.Server.Data.Entities;

namespace Y2KMusicServer.Server.Data;

/// <summary>
/// The operator-editable genre map: the list of genre buckets the library is
/// filtered by, plus rules mapping raw tag genres onto them. Persisted as JSON
/// at <c>&lt;DataPath&gt;\genre-map.json</c>. Deliberately applied at
/// <b>query time</b>, not scan time — a track keeps its raw tag genre
/// (<see cref="Track.Genre"/>) and the effective bucket is
/// <c>GenreOverride ?? map(rawGenre) ?? "Unknown"</c> — so editing the map
/// re-buckets the whole library instantly, with no rescan.
/// </summary>
public static class GenreMapStore
{
    public const string Unknown = "Unknown";

    public sealed class GenreRule
    {
        /// <summary>Raw tag genre this rule matches (case-insensitive). When
        /// <see cref="Substring"/> is set, a contains-match; otherwise exact.</summary>
        public string Raw { get; set; } = "";
        public bool Substring { get; set; }
        /// <summary>Target bucket (must be one of <see cref="GenreMap.Buckets"/>).</summary>
        public string Bucket { get; set; } = "";
    }

    public sealed class GenreMap
    {
        /// <summary>The buckets shown as library filters. Fully operator-editable;
        /// <see cref="Unknown"/> is implicit and always present.</summary>
        public List<string> Buckets { get; set; } = new();
        public List<GenreRule> Rules { get; set; } = new();
    }

    private static readonly string[] DefaultBuckets =
        { "Pop", "Rock", "Metal", "Dance", "Techno", "Country", "Classical" };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static GenreMap Load(IConfiguration cfg)
    {
        var path = DataPaths.GenreMapPath(cfg);
        lock (Gate)
        {
            try
            {
                if (File.Exists(path))
                {
                    var m = JsonSerializer.Deserialize<GenreMap>(File.ReadAllText(path));
                    if (m != null) return Sanitise(m);
                }
            }
            catch { /* missing / corrupt → defaults */ }
            return Sanitise(new GenreMap { Buckets = DefaultBuckets.ToList() });
        }
    }

    public static GenreMap Save(IConfiguration cfg, GenreMap m)
    {
        m = Sanitise(m);
        var path = DataPaths.GenreMapPath(cfg);
        lock (Gate)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(m, Indented));
        }
        return m;
    }

    /// <summary>
    /// Resolves a raw tag genre to a bucket. Exact rules win over substring
    /// rules; a raw genre that already equals a bucket name maps to it without
    /// needing a rule; anything unmatched lands in <see cref="Unknown"/>.
    /// </summary>
    public static string Resolve(GenreMap map, string? rawGenre)
    {
        if (string.IsNullOrWhiteSpace(rawGenre)) return Unknown;
        var raw = rawGenre.Trim();

        foreach (var r in map.Rules)
            if (!r.Substring && string.Equals(r.Raw, raw, StringComparison.OrdinalIgnoreCase))
                return NormaliseBucket(map, r.Bucket);

        foreach (var r in map.Rules)
            if (r.Substring && raw.Contains(r.Raw, StringComparison.OrdinalIgnoreCase))
                return NormaliseBucket(map, r.Bucket);

        var direct = map.Buckets.FirstOrDefault(
            b => string.Equals(b, raw, StringComparison.OrdinalIgnoreCase));
        if (direct != null) return direct;

        // Multi-genre tags ("Rock, Latin, Funk" / "Dance/Electronic"): try the
        // parts, in tag order — first part with an exact rule or a bucket of
        // its own wins. Whole-string rules above always take precedence, so an
        // operator rule for the full tag still overrides this.
        foreach (var part in raw.Split(SplitChars, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var r in map.Rules)
                if (!r.Substring && string.Equals(r.Raw, part, StringComparison.OrdinalIgnoreCase))
                    return NormaliseBucket(map, r.Bucket);
            var hit = map.Buckets.FirstOrDefault(
                b => string.Equals(b, part, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }

        return Unknown;
    }

    private static readonly char[] SplitChars = { ',', ';', '/', '&', '+' };

    /// <summary>The effective genre bucket for a track:
    /// override → mapped raw genre → Unknown.</summary>
    public static string EffectiveGenre(GenreMap map, Track t)
        => !string.IsNullOrWhiteSpace(t.GenreOverride)
            ? NormaliseBucket(map, t.GenreOverride!)
            : Resolve(map, t.Genre);

    /// <summary>Decade start year for a release year (1987 → 1980), or null when
    /// the year is unknown/implausible.</summary>
    public static int? Decade(int? year)
        => year is int y and >= 1900 and <= 2100 ? (y / 10) * 10 : null;

    private static string NormaliseBucket(GenreMap map, string bucket)
    {
        var hit = map.Buckets.FirstOrDefault(
            b => string.Equals(b, bucket.Trim(), StringComparison.OrdinalIgnoreCase));
        return hit ?? Unknown;
    }

    private static GenreMap Sanitise(GenreMap m)
    {
        m.Buckets ??= new List<string>();
        m.Rules ??= new List<GenreRule>();

        m.Buckets = m.Buckets
            .Select(b => (b ?? "").Trim())
            .Where(b => b.Length > 0 && !string.Equals(b, Unknown, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        m.Rules.RemoveAll(r => string.IsNullOrWhiteSpace(r.Raw) || string.IsNullOrWhiteSpace(r.Bucket));
        foreach (var r in m.Rules) { r.Raw = r.Raw.Trim(); r.Bucket = r.Bucket.Trim(); }
        return m;
    }
}
