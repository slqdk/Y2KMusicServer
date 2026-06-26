using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Y2KMusicServer.Server.Audio;
using Y2KMusicServer.Server.Data;
using Y2KMusicServer.Server.Data.Entities;

namespace Y2KMusicServer.Server.Playback;

/// <summary>
/// Owns the <see cref="PlaylistEntry"/>-backed ordered playlist (the currently
/// playing track plus everything queued after it) and the Auto DJ track
/// selector. This is the .NET-service port of the legacy WinForms
/// <c>AutoDjAddTrack</c> / <c>CheckAutoDjTopUp</c> pair and their helpers.
///
/// Registered as a singleton: it holds the in-memory "recently played" rings
/// and the Auto DJ reference BPM, exactly as the legacy app kept them in
/// fields. That state is deliberately not persisted — a restart starts the
/// history empty, matching the old build.
///
/// <see cref="AutoDjScheduler"/> drives this (it owns the loop and the engine
/// chaining); <c>AdminPlaylistController</c> exposes it over HTTP. All playlist
/// mutations funnel through <see cref="_mutateGate"/> so the scheduler loop and
/// an admin request never write the table at the same time.
/// </summary>
public sealed class PlaylistService
{
    /// <summary>Same artist won't be auto-queued within this many tracks (legacy constant).</summary>
    private const int ArtistCooldownTracks = 8;

    /// <summary>How many of each history ring we keep (legacy kept the last 20).</summary>
    private const int HistoryCap = 20;

    private readonly IDbContextFactory<Y2KDbContext> _dbf;
    private readonly ILogger<PlaylistService> _log;
    private readonly IConfiguration _cfg;

    private readonly SemaphoreSlim _mutateGate = new(1, 1);

    // In-memory history, guarded by _historyLock. TrackIds drive the exclusion
    // set; normalised artist names drive the cooldown penalty.
    private readonly object _historyLock = new();
    private readonly List<int> _recentlyPlayed = new();
    private readonly List<string> _recentlyPlayedArtists = new();
    private double _refBpm; // BPM of the last human/seed pick; 0 = unset.

    // Listener-selected categories (the public category bar). When non-empty,
    // Auto DJ draws only from these, overriding the time-of-day schedule —
    // the legacy "web category override". Set by the public controller.
    private volatile int[] _webCategories = Array.Empty<int>();

    public PlaylistService(IDbContextFactory<Y2KDbContext> dbf, ILogger<PlaylistService> log, IConfiguration cfg)
    {
        _dbf = dbf;
        _log = log;
        _cfg = cfg;
    }

    // ── History (called by the scheduler on each promotion) ───────────────────

    /// <summary>
    /// Records that a track finished playing: pushes it onto the recently-played
    /// rings (capped) and, if it carries a real tempo, seeds the Auto DJ
    /// reference BPM. Resolves the artist/BPM from the database by id.
    /// </summary>
    public async Task NotePlayedAsync(int trackId, CancellationToken ct = default)
    {
        string? artist = null;
        double? bpm = null;
        try
        {
            await using var db = await _dbf.CreateDbContextAsync(ct);
            var t = await db.Tracks.AsNoTracking()
                .Where(x => x.Id == trackId)
                .Select(x => new { x.Artist, x.Bpm })
                .FirstOrDefaultAsync(ct);
            if (t != null) { artist = t.Artist; bpm = t.Bpm; }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "NotePlayed could not resolve track {TrackId}", trackId);
        }

        lock (_historyLock)
        {
            _recentlyPlayed.Add(trackId);
            TrimTail(_recentlyPlayed);

            var norm = NormaliseArtist(artist);
            if (!string.IsNullOrEmpty(norm))
            {
                _recentlyPlayedArtists.Add(norm);
                TrimTail(_recentlyPlayedArtists);
            }

            if (bpm is > 30) _refBpm = bpm.Value;
        }
    }

    private static void TrimTail<T>(List<T> list)
    {
        if (list.Count > HistoryCap)
            list.RemoveRange(0, list.Count - HistoryCap);
    }

    /// <summary>Sets the listener-selected category override (empty = no override).</summary>
    public void SetWebCategories(IEnumerable<int> ids) =>
        _webCategories = ids.Distinct().ToArray();

    /// <summary>The current listener-selected category override.</summary>
    public int[] GetWebCategories() => _webCategories;

    // ── Queries used by the scheduler ─────────────────────────────────────────

    public async Task<bool> IsAutoDjOnAsync(CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var s = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        return s?.AutoDj ?? false;
    }

    /// <summary>
    /// Number of entries queued AFTER the currently playing track. If the
    /// current track isn't in the playlist (operator loaded something
    /// off-playlist), every entry counts as upcoming.
    /// </summary>
    public async Task<int> UpcomingCountAsync(int? currentTrackId, CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var entries = await db.PlaylistEntries.AsNoTracking()
            .OrderBy(e => e.Position).ToListAsync(ct);
        int curPos = CurrentPosition(entries, currentTrackId);
        return entries.Count(e => e.Position > curPos);
    }

    /// <summary>The TrackId of the first entry after the current track, or null.</summary>
    public async Task<int?> NextUpcomingTrackIdAsync(int? currentTrackId, CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var entries = await db.PlaylistEntries.AsNoTracking()
            .OrderBy(e => e.Position).ToListAsync(ct);
        int curPos = CurrentPosition(entries, currentTrackId);
        return entries.Where(e => e.Position > curPos)
            .OrderBy(e => e.Position)
            .Select(e => (int?)e.TrackId)
            .FirstOrDefault();
    }

    /// <summary>
    /// Drops entries that sit BEFORE the currently playing track — they've been
    /// consumed. The table is kept to "now playing + upcoming", mirroring the
    /// legacy <c>songsAfterCurrent</c> view. No-op if the current track isn't in
    /// the playlist.
    /// </summary>
    public async Task PruneConsumedAsync(int currentTrackId, CancellationToken ct = default)
    {
        await _mutateGate.WaitAsync(ct);
        try
        {
            await using var db = await _dbf.CreateDbContextAsync(ct);
            var entries = await db.PlaylistEntries.OrderBy(e => e.Position).ToListAsync(ct);
            int curPos = CurrentPosition(entries, currentTrackId);
            if (curPos <= 0) return; // current is head (or absent) — nothing before it.

            var stale = entries.Where(e => e.Position < curPos).ToList();
            if (stale.Count == 0) return;

            db.PlaylistEntries.RemoveRange(stale);
            await db.SaveChangesAsync(ct);
            await RenumberAsync(db, ct);
        }
        finally { _mutateGate.Release(); }
    }

    // ── Admin operations ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PlaylistItemDto>> GetAsync(CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var items = await db.PlaylistEntries.AsNoTracking()
            .OrderBy(e => e.Position)
            .Select(e => new PlaylistItemDto
            {
                Id = e.Id,
                Position = e.Position,
                TrackId = e.TrackId,
                Title = e.Track!.Title,
                Artist = e.Track!.Artist,
                DurationSec = e.Track!.DurationSec,
                Bpm = e.Track!.Bpm,
                Lufs = e.Track!.LufsIntegrated,
                Source = e.Source.ToString(),
                AddedBy = e.AddedBy,
                AddedAt = e.AddedAt
            })
            .ToListAsync(ct);

        // Mix-in point = the track's intro-skip (IntroEndSec) from the structure
        // cache, read-only so the 2s poll never decodes audio. Null when the
        // structure hasn't been computed for that track yet (fills in over time).
        return items
            .Select(i => i with { IntroEndSec = TrackStructure.TryReadCached(_cfg, i.TrackId)?.IntroEndSec })
            .ToList();
    }

    /// <summary>
    /// Inserts a track the operator (or a request) chose. Manual / request adds
    /// land just before the first Auto entry after the current track — the
    /// legacy <c>FindAutoInsertIndex</c> rule — so a hand-picked song plays
    /// before the auto-fill resumes. Auto adds append at the end. When
    /// <paramref name="atEnd"/> is set the pick is appended after everything
    /// (still a manual entry, just parked at the tail rather than queued next).
    /// </summary>
    public async Task<PlaylistAddResult> AddAsync(
        int trackId, PlaylistSource source, string? addedBy, int? currentTrackId,
        CancellationToken ct = default, bool atEnd = false)
    {
        await _mutateGate.WaitAsync(ct);
        try
        {
            await using var db = await _dbf.CreateDbContextAsync(ct);

            var track = await db.Tracks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == trackId, ct);
            if (track == null) return PlaylistAddResult.NotFound;

            var entries = await db.PlaylistEntries.OrderBy(e => e.Position).ToListAsync(ct);

            int insertPos;
            if (atEnd || source == PlaylistSource.Auto)
            {
                insertPos = entries.Count == 0 ? 0 : entries[^1].Position + 1;
            }
            else
            {
                int curPos = CurrentPosition(entries, currentTrackId);
                var firstAutoAfter = entries
                    .Where(e => e.Position > curPos && IsAutoFill(e.Source))
                    .OrderBy(e => e.Position)
                    .FirstOrDefault();
                insertPos = firstAutoAfter?.Position
                            ?? (entries.Count == 0 ? 0 : entries[^1].Position + 1);

                // Shift everything at/after the insert point down by one.
                foreach (var e in entries.Where(e => e.Position >= insertPos))
                    e.Position += 1;
            }

            db.PlaylistEntries.Add(new PlaylistEntry
            {
                TrackId = trackId,
                Position = insertPos,
                Source = source,
                AddedBy = addedBy,
                AddedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
            await RenumberAsync(db, ct);

            // A human pick reseeds the Auto DJ reference BPM (legacy behaviour) —
            // but only when it's the next thing up, not parked at the tail.
            if (!atEnd && source != PlaylistSource.Auto && track.Bpm is > 30)
                lock (_historyLock) { _refBpm = track.Bpm.Value; }

            return PlaylistAddResult.Ok;
        }
        finally { _mutateGate.Release(); }
    }

    public async Task<bool> RemoveAsync(int entryId, CancellationToken ct = default)
    {
        await _mutateGate.WaitAsync(ct);
        try
        {
            await using var db = await _dbf.CreateDbContextAsync(ct);
            var e = await db.PlaylistEntries.FirstOrDefaultAsync(x => x.Id == entryId, ct);
            if (e == null) return false;
            db.PlaylistEntries.Remove(e);
            await db.SaveChangesAsync(ct);
            await RenumberAsync(db, ct);
            return true;
        }
        finally { _mutateGate.Release(); }
    }

    /// <summary>Clears upcoming entries, keeping the currently playing track.</summary>
    public async Task<int> ClearUpcomingAsync(int? currentTrackId, CancellationToken ct = default)
    {
        await _mutateGate.WaitAsync(ct);
        try
        {
            await using var db = await _dbf.CreateDbContextAsync(ct);
            var entries = await db.PlaylistEntries.OrderBy(e => e.Position).ToListAsync(ct);
            int curPos = CurrentPosition(entries, currentTrackId);
            var doomed = entries.Where(e => e.Position > curPos).ToList();
            if (doomed.Count == 0) return 0;
            db.PlaylistEntries.RemoveRange(doomed);
            await db.SaveChangesAsync(ct);
            await RenumberAsync(db, ct);
            return doomed.Count;
        }
        finally { _mutateGate.Release(); }
    }

    /// <summary>
    /// Track id of the first entry after the currently playing one, or null if
    /// there is nothing queued. Used to pick the crossfade target right after a
    /// queue rebuild (the listener category switch).
    /// </summary>
    public async Task<int?> NextUpcomingTrackIdAsync(int? currentTrackId, CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var entries = await db.PlaylistEntries.AsNoTracking().OrderBy(e => e.Position).ToListAsync(ct);
        int curPos = CurrentPosition(entries, currentTrackId);
        return entries.FirstOrDefault(e => e.Position > curPos)?.TrackId;
    }

    // ── Auto DJ top-up (the selection port) ───────────────────────────────────

    /// <summary>
    /// Picks up to <c>Settings.AutoDjTracks</c> tracks and appends them as
    /// <see cref="PlaylistSource.Auto"/> entries. Faithful port of the legacy
    /// scorer: BPM window (random while BPM is unknown — Phase 5) × category
    /// priority weight × artist cooldown, with widen-then-ignore-BPM fallbacks,
    /// then artist-spread within the batch. Returns the number added.
    /// </summary>
    public async Task<int> TopUpAsync(CancellationToken ct = default)
    {
        await _mutateGate.WaitAsync(ct);
        try
        {
            await using var db = await _dbf.CreateDbContextAsync(ct);

            var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
            if (settings is not { AutoDj: true }) return 0;

            int tracksToAdd = Math.Clamp(settings.AutoDjTracks <= 0 ? 3 : settings.AutoDjTracks, 1, 20);
            double bpmRange = Math.Max(0, settings.AutoDjBpmDev);

            var categories = await db.Categories.AsNoTracking()
                .Include(c => c.Slots).ToListAsync(ct);
            var catById = categories.ToDictionary(c => c.Id);

            var library = await db.Tracks.AsNoTracking().ToListAsync(ct);
            if (library.Count == 0) return 0;

            var entries = await db.PlaylistEntries.AsNoTracking()
                .OrderBy(e => e.Position).ToListAsync(ct);
            var trackById = library.ToDictionary(t => t.Id);

            var now = DateTime.Now;

            // Decision #1: with no schedule slots anywhere (no slot UI before
            // Phase 4), fall back to "every ENABLED category is active" so Auto
            // DJ is testable. Logged so it's never silent.
            bool noSlotsAnywhere = categories.All(c => c.Slots.Count == 0);
            if (noSlotsAnywhere && settings.AutoDj)
                _log.LogInformation("Auto DJ: no category schedule slots configured — " +
                                    "treating enabled categories as active (pre-Phase-4 fallback).");

            // Listener category override: when set, it replaces the schedule.
            var webCats = _webCategories;
            if (webCats.Length > 0)
                _log.LogDebug("Auto DJ: listener category override active ({Count} categories).", webCats.Length);

            // Guard: if nothing is active, Auto DJ is a no-op (legacy behaviour).
            bool anyActive =
                library.Any(t => t.CategoryId == null) ||
                categories.Any(c => IsCategoryActiveForAutoDj(c, now, noSlotsAnywhere, webCats));
            if (!anyActive)
            {
                _log.LogDebug("Auto DJ top-up skipped: no active category right now.");
                return 0;
            }

            // Reference BPM: in-memory seed, else the current head's tempo.
            double refBpm;
            lock (_historyLock) { refBpm = _refBpm; }
            if (refBpm <= 30)
            {
                var head = entries.FirstOrDefault();
                if (head != null && trackById.TryGetValue(head.TrackId, out var ht) && ht.Bpm is > 30)
                    refBpm = ht.Bpm.Value;
            }
            bool randomMode = bpmRange <= 0 || refBpm <= 30;

            // Exclusion = already queued + recently played.
            int[] recentSnapshot;
            string[] recentArtistsSnapshot;
            lock (_historyLock)
            {
                recentSnapshot = _recentlyPlayed.ToArray();
                recentArtistsSnapshot = _recentlyPlayedArtists.ToArray();
            }
            var excluded = new HashSet<int>(entries.Select(e => e.TrackId));
            foreach (var id in recentSnapshot) excluded.Add(id);

            // Similarity window: recently played + upcoming, resolved to tags.
            var simWindow = BuildSimilarityWindow(entries, recentSnapshot, trackById);

            // Upcoming artists (ordered) for the cooldown's look-ahead.
            var upcomingArtists = entries
                .Select(e => trackById.TryGetValue(e.TrackId, out var t) ? NormaliseArtist(t.Artist) : "")
                .ToList();

            bool Eligible(Track t)
            {
                if (excluded.Contains(t.Id)) return false;
                if (!File.Exists(t.FilePath)) return false;
                if (IsTooSimilar(t, simWindow)) return false;
                // Category filter: uncategorised always passes; else must be enabled.
                if (t.CategoryId is int cid)
                {
                    if (!catById.TryGetValue(cid, out var cat) || !cat.Enabled) return false;
                    if (!IsCategoryActiveForAutoDj(cat, now, noSlotsAnywhere, webCats)) return false;
                }
                return true;
            }

            double PriorityWeight(Track t) =>
                t.CategoryId is int cid && catById.TryGetValue(cid, out var cat)
                    ? GetCategoryPriorityWeight(cat, now)
                    : 3.0;

            var candidates = new List<(Track track, double score)>();
            foreach (var t in library)
            {
                if (!Eligible(t)) continue;

                double score;
                if (!randomMode && t.Bpm is > 30)
                {
                    double diff = Math.Abs(t.Bpm.Value - refBpm);
                    if (diff > bpmRange) continue;
                    score = 1.0 - (diff / bpmRange);
                }
                else
                {
                    score = 0.5; // random mode: equally eligible
                }

                score *= PriorityWeight(t);
                score *= ArtistCooldownPenalty(t.Artist, upcomingArtists, recentArtistsSnapshot);
                candidates.Add((t, score));
            }

            // Fallback 1: widen the BPM window ×2 (skipped in random mode).
            if (candidates.Count == 0 && !randomMode && refBpm > 30)
            {
                double widened = bpmRange * 2.0;
                foreach (var t in library)
                {
                    if (!Eligible(t) || t.Bpm is not > 30) continue;
                    double diff = Math.Abs(t.Bpm.Value - refBpm);
                    if (diff <= widened)
                        candidates.Add((t, 0.3 - (diff / widened) * 0.2));
                }
            }

            // Fallback 2: ignore BPM entirely.
            if (candidates.Count == 0)
                foreach (var t in library)
                    if (Eligible(t))
                        candidates.Add((t, 0.1));

            if (candidates.Count == 0) return 0;

            var rng = Random.Shared;
            candidates.Sort((a, b) =>
            {
                int c = b.score.CompareTo(a.score);
                return c != 0 ? c : rng.Next(-1, 2);
            });

            // Pick, enforcing one-artist-per-batch over a shuffled top pool.
            int poolSize = Math.Min(candidates.Count, Math.Max(tracksToAdd * 6, 20));
            var pool = candidates.GetRange(0, poolSize);
            for (int i = pool.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (pool[i], pool[j]) = (pool[j], pool[i]); }

            var picks = new List<Track>();
            var usedTracks = new HashSet<int>();
            var usedArtists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (track, _) in pool)
            {
                if (picks.Count >= tracksToAdd) break;
                if (!usedTracks.Add(track.Id)) continue;
                var norm = NormaliseArtist(track.Artist);
                if (!string.IsNullOrEmpty(norm) && !usedArtists.Add(norm)) { usedTracks.Remove(track.Id); continue; }
                picks.Add(track);
            }

            // Safety: if artist-spread left us short, fill ignoring that rule.
            if (picks.Count < tracksToAdd)
                foreach (var (track, _) in pool)
                {
                    if (picks.Count >= tracksToAdd) break;
                    if (usedTracks.Add(track.Id)) picks.Add(track);
                }

            if (picks.Count == 0) return 0;

            int nextPos = entries.Count == 0 ? 0 : entries[^1].Position + 1;
            foreach (var pick in picks)
                db.PlaylistEntries.Add(new PlaylistEntry
                {
                    TrackId = pick.Id,
                    Position = nextPos++,
                    // Schedule-driven when a real time-slot is active; Auto (the
                    // enabled-category fallback) when no slots are configured.
                    Source = noSlotsAnywhere ? PlaylistSource.Auto : PlaylistSource.Schedule,
                    AddedBy = noSlotsAnywhere ? "Auto" : "Schedule",
                    AddedAt = DateTime.UtcNow
                });
            await db.SaveChangesAsync(ct);

            _log.LogInformation("Auto DJ added {Count} track(s){Mode}.",
                picks.Count, randomMode ? " (random mode — BPM not yet analysed)" : $" (±{bpmRange} BPM of {refBpm:F0})");
            return picks.Count;
        }
        finally { _mutateGate.Release(); }
    }

    // ── Position helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Both Auto DJ fill kinds — the enabled-category fallback (<c>Auto</c>) and
    /// schedule-driven picks (<c>Schedule</c>) — count as "filler" that operator
    /// and request picks insert ahead of.
    /// </summary>
    private static bool IsAutoFill(PlaylistSource s) =>
        s is PlaylistSource.Auto or PlaylistSource.Schedule;

    /// <summary>
    /// The Position of the current track in the (ordered) entry list, or -1 if
    /// it isn't present — in which case the whole list is "upcoming".
    /// </summary>
    private static int CurrentPosition(List<PlaylistEntry> ordered, int? currentTrackId)
    {
        if (currentTrackId is not int id) return -1;
        var match = ordered.FirstOrDefault(e => e.TrackId == id);
        return match?.Position ?? -1;
    }

    /// <summary>Reassigns contiguous 0..n-1 positions after any structural change.</summary>
    private static async Task RenumberAsync(Y2KDbContext db, CancellationToken ct)
    {
        var ordered = await db.PlaylistEntries.OrderBy(e => e.Position).ToListAsync(ct);
        bool dirty = false;
        for (int i = 0; i < ordered.Count; i++)
            if (ordered[i].Position != i) { ordered[i].Position = i; dirty = true; }
        if (dirty) await db.SaveChangesAsync(ct);
    }

    // ── Selection helpers (ported from the legacy WinForms build) ─────────────

    private static List<(string artist, string title)> BuildSimilarityWindow(
        List<PlaylistEntry> entries, int[] recentlyPlayed, Dictionary<int, Track> trackById)
    {
        var window = new List<(string, string)>();
        foreach (var id in recentlyPlayed)
            if (trackById.TryGetValue(id, out var t))
                window.Add((t.Artist ?? "", t.Title ?? ""));
        foreach (var e in entries)
        {
            if (window.Count >= 40) break;
            if (trackById.TryGetValue(e.TrackId, out var t))
                window.Add((t.Artist ?? "", t.Title ?? ""));
        }
        return window;
    }

    private static bool IsTooSimilar(Track candidate, List<(string artist, string title)> window)
    {
        string artist = (candidate.Artist ?? "").Trim();
        string baseT = StripTitleSuffix(candidate.Title ?? "");
        if (artist.Length == 0 && baseT.Length == 0) return false;

        foreach (var (wArtist, wTitle) in window)
        {
            bool sameArtist = string.Equals((wArtist ?? "").Trim(), artist, StringComparison.OrdinalIgnoreCase);
            bool sameTitle = string.Equals(StripTitleSuffix(wTitle ?? ""), baseT, StringComparison.OrdinalIgnoreCase);
            if (sameArtist && sameTitle) return true;
        }
        return false;
    }

    /// <summary>
    /// Strips a trailing parenthesised / bracketed group so "Song (Radio Mix)"
    /// and "Song" compare equal.
    /// </summary>
    private static string StripTitleSuffix(string title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        int p = title.LastIndexOf('(');
        int b = title.LastIndexOf('[');
        int cut = Math.Max(p, b);
        if (cut > 2) title = title.Substring(0, cut);
        return title.Trim().TrimEnd('-', '_', ' ');
    }

    /// <summary>Lower-cases, trims, drops "The ", and cuts feat/ft/&amp;/and suffixes.</summary>
    private static string NormaliseArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return "";
        string s = artist.Trim().ToLowerInvariant();
        foreach (var sep in new[] { " feat.", " ft.", " featuring", " & ", " and " })
        {
            int idx = s.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0) { s = s.Substring(0, idx).Trim(); break; }
        }
        if (s.StartsWith("the ")) s = s.Substring(4).Trim();
        return s;
    }

    /// <summary>
    /// 0.02–1.0 multiplier penalising an artist that appears soon in the
    /// upcoming queue or recently in history; quadratic decay over
    /// <see cref="ArtistCooldownTracks"/>. 1.0 for blank/unknown artists.
    /// </summary>
    private static double ArtistCooldownPenalty(string? artist, List<string> upcomingArtists, string[] recentArtists)
    {
        string norm = NormaliseArtist(artist);
        if (string.IsNullOrEmpty(norm)) return 1.0;

        // Look ahead in the queue (index 0 = next slot).
        for (int i = 0; i < upcomingArtists.Count && i <= ArtistCooldownTracks; i++)
        {
            if (string.Equals(upcomingArtists[i], norm, StringComparison.OrdinalIgnoreCase))
            {
                if (i == 0) return 0.02; // hard block: same artist in the very next slot
                double frac = (double)i / ArtistCooldownTracks;
                return Math.Max(0.05, frac * frac);
            }
        }

        // Look back in history (last element = most recent).
        for (int i = recentArtists.Length - 1; i >= 0; i--)
        {
            if (string.Equals(recentArtists[i], norm, StringComparison.OrdinalIgnoreCase))
            {
                int tracksAgo = recentArtists.Length - 1 - i + 1; // 1 = played last
                if (tracksAgo >= ArtistCooldownTracks) return 1.0;
                double frac = (double)tracksAgo / ArtistCooldownTracks;
                return Math.Max(0.05, frac * frac);
            }
        }
        return 1.0;
    }

    /// <summary>
    /// True if a category should feed Auto DJ right now. Uncategorised tracks
    /// are handled by the caller. When a listener category override is set
    /// (<paramref name="webCats"/> non-empty) it replaces the schedule entirely.
    /// Otherwise: at least one enabled slot must cover the current day + time,
    /// or — with the pre-Phase-4 no-slots fallback — the category just needs to
    /// be enabled.
    /// </summary>
    private static bool IsCategoryActiveForAutoDj(Category cat, DateTime now, bool noSlotsFallback, int[] webCats)
    {
        // Listener override wins over the schedule when present.
        if (webCats.Length > 0) return Array.IndexOf(webCats, cat.Id) >= 0;
        if (noSlotsFallback) return cat.Enabled;
        if (cat.Slots.Count == 0) return false;

        int todayDow = ((int)now.DayOfWeek + 6) % 7; // Mon=0 … Sun=6
        TimeSpan nowTime = now.TimeOfDay;

        foreach (var slot in cat.Slots)
        {
            if (!slot.Enabled) continue;
            if (!SlotCoversNow(slot, todayDow, nowTime)) continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Priority weight of the active slot covering now: priority 1 → ×5.0 …
    /// priority 5 → ×1.0. Neutral 3.0 when uncategorised or no active slot
    /// (including the pre-Phase-4 no-slots fallback).
    /// </summary>
    private static double GetCategoryPriorityWeight(Category cat, DateTime now)
    {
        if (cat.Slots.Count == 0) return 3.0;

        int todayDow = ((int)now.DayOfWeek + 6) % 7;
        TimeSpan nowTime = now.TimeOfDay;

        foreach (var slot in cat.Slots)
        {
            if (!slot.Enabled) continue;
            if (!SlotCoversNow(slot, todayDow, nowTime)) continue;
            int pri = Math.Clamp(slot.Priority, 1, 5);
            return 6.0 - pri; // 1→5.0 … 5→1.0
        }
        return 3.0;
    }

    private static bool SlotCoversNow(CategorySlot slot, int todayDow, TimeSpan nowTime)
    {
        // DaysMask: bit 0 = Monday … bit 6 = Sunday. 0 = every day (legacy: no
        // days ticked ⇒ applies daily).
        if (slot.DaysMask != 0 && (slot.DaysMask & (1 << todayDow)) == 0) return false;

        if (!TimeSpan.TryParse(slot.TimeFromHHmm, out var from)) return false;
        if (!TimeSpan.TryParse(slot.TimeToHHmm, out var to)) return false;

        return from <= to
            ? nowTime >= from && nowTime <= to    // same-day range
            : nowTime >= from || nowTime <= to;   // overnight wrap (e.g. 22:00–02:00)
    }
}

// ── DTOs (server → admin JSON; mirrors how PlaybackStatus lives in Audio) ──────

public sealed record PlaylistItemDto
{
    public int Id { get; init; }
    public int Position { get; init; }
    public int TrackId { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public double DurationSec { get; init; }
    public double? Bpm { get; init; }
    public double? Lufs { get; init; }
    public double? IntroEndSec { get; init; }
    public string Source { get; init; } = "Auto";
    public string? AddedBy { get; init; }
    public DateTime AddedAt { get; init; }
}

public enum PlaylistAddResult { Ok, NotFound }
