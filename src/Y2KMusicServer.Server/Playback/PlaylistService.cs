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

    // ── Saved-playlist activation ─────────────────────────────────────────────

    public enum ActivateResult { Ok, NotFound, Empty }

    /// <summary>
    /// Replaces the live queue with a saved playlist: upcoming entries are
    /// cleared EXCEPT pending Request entries (requests survive and play first),
    /// then the saved playlist's tracks are appended in order as Schedule
    /// entries labelled with the playlist's name. The currently playing track is
    /// untouched; the caller fires the crossfade into the first upcoming entry.
    /// Tracks whose file is missing, or that already sit in the kept portion,
    /// are skipped. Returns the count appended via <paramref name="added"/>.
    /// </summary>
    public async Task<ActivateResult> ActivateSavedAsync(
        int savedPlaylistId, int? currentTrackId, CancellationToken ct = default)
    {
        await _mutateGate.WaitAsync(ct);
        try
        {
            await using var db = await _dbf.CreateDbContextAsync(ct);

            var saved = await db.SavedPlaylists.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == savedPlaylistId, ct);
            if (saved == null) return ActivateResult.NotFound;

            var savedTracks = await db.SavedPlaylistTracks.AsNoTracking()
                .Where(x => x.SavedPlaylistId == savedPlaylistId)
                .OrderBy(x => x.Position)
                .Select(x => new { x.TrackId, x.Track!.FilePath })
                .ToListAsync(ct);
            if (savedTracks.Count == 0) return ActivateResult.Empty;

            var entries = await db.PlaylistEntries.OrderBy(e => e.Position).ToListAsync(ct);
            int curPos = CurrentPosition(entries, currentTrackId);

            // Drop every upcoming entry that is not a surviving request.
            var doomed = entries
                .Where(e => e.Position > curPos && e.Source != PlaylistSource.Request)
                .ToList();
            if (doomed.Count > 0) db.PlaylistEntries.RemoveRange(doomed);

            // Kept portion (current + requests): never immediately duplicated.
            var keptIds = entries.Except(doomed).Select(e => e.TrackId).ToHashSet();

            int nextPos = entries.Except(doomed).Select(e => e.Position)
                .DefaultIfEmpty(-1).Max() + 1;

            int added = 0, missing = 0;
            foreach (var s in savedTracks)
            {
                if (keptIds.Contains(s.TrackId)) continue;
                if (!File.Exists(s.FilePath)) { missing++; continue; }
                db.PlaylistEntries.Add(new PlaylistEntry
                {
                    TrackId = s.TrackId,
                    Position = nextPos++,
                    Source = PlaylistSource.Schedule,
                    AddedBy = saved.Name,
                    AddedAt = DateTime.UtcNow
                });
                added++;
            }

            await db.SaveChangesAsync(ct);
            await RenumberAsync(db, ct);

            if (missing > 0)
                _log.LogWarning("Activate \"{Name}\": {Missing} track(s) skipped (file missing).",
                    saved.Name, missing);
            _log.LogInformation("Activated playlist \"{Name}\": {Added} track(s) queued, {Kept} request(s) kept ahead.",
                saved.Name, added, entries.Except(doomed).Count(e => e.Source == PlaylistSource.Request && e.Position > curPos));

            return ActivateResult.Ok;
        }
        finally { _mutateGate.Release(); }
    }

    // ── Auto DJ top-up (playlist-sourced) ─────────────────────────────────────

    // No-repeat memory: per saved playlist, the track ids Auto DJ has fed from
    // it since its last reshuffle. When every track in a playlist has been fed,
    // the set resets and the playlist starts over. In-memory (guarded by
    // _historyLock), deliberately not persisted — a restart starts fresh, like
    // the recently-played rings.
    private readonly Dictionary<int, HashSet<int>> _fedFromPlaylist = new();

    /// <summary>
    /// Picks up to <c>Settings.AutoDjTracks</c> tracks from the saved playlists
    /// whose schedule says they are active right now, and appends them as
    /// <see cref="PlaylistSource.Schedule"/> entries labelled with the source
    /// playlist's name. Per pick: the source playlist is chosen by
    /// priority-weighted random (priority 1–5 = weight, so a 5 feeds five times
    /// as often as a 1), then the track inside it by the legacy scorer — BPM
    /// window against the reference tempo (random while unknown) × artist
    /// cooldown, widen-then-ignore-BPM fallbacks, similarity suppression, and
    /// no repeats until the playlist is exhausted (then it reshuffles). With no
    /// active playlist the top-up is a no-op. Returns the number added.
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

            // ── Active playlists (schedule + content) ─────────────────────────
            var now = DateTime.Now;
            var playlists = await db.SavedPlaylists.AsNoTracking()
                .Include(pl => pl.Slots)
                .ToListAsync(ct);
            var active = playlists.Where(pl => IsPlaylistActiveNow(pl, now)).ToList();
            if (active.Count == 0)
            {
                _log.LogDebug("Auto DJ top-up skipped: no saved playlist has an active timeslot right now.");
                return 0;
            }

            // Member tracks per active playlist, in one query.
            var activeIds = active.Select(pl => pl.Id).ToHashSet();
            var membership = await db.SavedPlaylistTracks.AsNoTracking()
                .Where(x => activeIds.Contains(x.SavedPlaylistId))
                .Select(x => new { x.SavedPlaylistId, Track = x.Track! })
                .ToListAsync(ct);
            var tracksByPl = membership
                .GroupBy(x => x.SavedPlaylistId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Track).ToList());
            active.RemoveAll(pl => !tracksByPl.ContainsKey(pl.Id) || tracksByPl[pl.Id].Count == 0);
            if (active.Count == 0)
            {
                _log.LogDebug("Auto DJ top-up skipped: the active playlist(s) are empty.");
                return 0;
            }

            var entries = await db.PlaylistEntries.AsNoTracking()
                .OrderBy(e => e.Position).ToListAsync(ct);
            var trackById = membership.Select(x => x.Track)
                .GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());

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

            // Exclusion = already queued + recently played; history snapshots.
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

            var rng = Random.Shared;
            var picks = new List<(Track Track, string PlaylistName)>();
            var batchArtists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ── One pick at a time: playlist by priority weight, then track ───
            for (int slot = 0; slot < tracksToAdd; slot++)
            {
                Track? pick = null;
                string? pickedFrom = null;

                // Try a few playlist draws so one temporarily-dry playlist
                // (everything queued / too similar) doesn't stall the batch.
                var remaining = new List<SavedPlaylist>(active);
                for (int attempt = 0; pick == null && remaining.Count > 0; attempt++)
                {
                    var pl = WeightedPick(remaining, rng);
                    var members = tracksByPl[pl.Id];

                    // No-repeat: skip tracks already fed from this playlist;
                    // reshuffle (reset) once every member has been fed.
                    HashSet<int> fed;
                    lock (_historyLock)
                    {
                        if (!_fedFromPlaylist.TryGetValue(pl.Id, out fed!))
                            _fedFromPlaylist[pl.Id] = fed = new HashSet<int>();
                        if (members.All(m => fed.Contains(m.Id)))
                        {
                            fed.Clear();
                            _log.LogInformation("Auto DJ: playlist \"{Name}\" exhausted — reshuffling.", pl.Name);
                        }
                    }

                    bool Eligible(Track t)
                    {
                        bool isFed; lock (_historyLock) isFed = fed.Contains(t.Id);
                        if (isFed) return false;
                        if (excluded.Contains(t.Id)) return false;
                        if (IsTooSimilar(t, simWindow)) return false;
                        var norm = NormaliseArtist(t.Artist);
                        if (norm.Length > 0 && batchArtists.Contains(norm)) return false;
                        if (!File.Exists(t.FilePath)) return false;
                        return true;
                    }

                    double Score(Track t, double bpmScore) =>
                        bpmScore * ArtistCooldownPenalty(t.Artist, upcomingArtists, recentArtistsSnapshot);

                    var scored = new List<(Track t, double s)>();
                    foreach (var t in members)
                    {
                        if (!Eligible(t)) continue;
                        if (!randomMode && t.Bpm is > 30)
                        {
                            double diff = Math.Abs(t.Bpm.Value - refBpm);
                            if (diff > bpmRange) continue;
                            scored.Add((t, Score(t, 1.0 - diff / bpmRange)));
                        }
                        else
                        {
                            scored.Add((t, Score(t, 0.5)));
                        }
                    }

                    // Fallback 1: widen the BPM window ×2 (skipped in random mode).
                    if (scored.Count == 0 && !randomMode && refBpm > 30)
                    {
                        double widened = bpmRange * 2.0;
                        foreach (var t in members)
                        {
                            if (!Eligible(t) || t.Bpm is not > 30) continue;
                            double diff = Math.Abs(t.Bpm.Value - refBpm);
                            if (diff <= widened)
                                scored.Add((t, Score(t, 0.3 - (diff / widened) * 0.2)));
                        }
                    }

                    // Fallback 2: ignore BPM entirely.
                    if (scored.Count == 0)
                        foreach (var t in members)
                            if (Eligible(t))
                                scored.Add((t, Score(t, 0.1)));

                    if (scored.Count == 0)
                    {
                        // This playlist has nothing eligible right now — draw
                        // another (without replacement) for this pick.
                        remaining.Remove(pl);
                        continue;
                    }

                    // Weighted-random over the scored candidates keeps variety
                    // while still favouring the best BPM/cooldown fits.
                    pick = WeightedPickTrack(scored, rng);
                    pickedFrom = pl.Name;

                    lock (_historyLock) fed.Add(pick.Id);
                }

                if (pick == null) break; // every active playlist is dry — stop the batch

                picks.Add((pick, pickedFrom!));
                excluded.Add(pick.Id);
                var normPick = NormaliseArtist(pick.Artist);
                if (normPick.Length > 0) batchArtists.Add(normPick);
                upcomingArtists.Add(normPick);
                simWindow.Add((pick.Artist ?? "", pick.Title ?? ""));
            }

            if (picks.Count == 0) return 0;

            int nextPos = entries.Count == 0 ? 0 : entries[^1].Position + 1;
            foreach (var (track, plName) in picks)
                db.PlaylistEntries.Add(new PlaylistEntry
                {
                    TrackId = track.Id,
                    Position = nextPos++,
                    Source = PlaylistSource.Schedule,
                    AddedBy = plName,
                    AddedAt = DateTime.UtcNow
                });
            await db.SaveChangesAsync(ct);

            _log.LogInformation("Auto DJ added {Count} track(s) from {Playlists}{Mode}.",
                picks.Count,
                string.Join(", ", picks.Select(p => p.PlaylistName).Distinct()),
                randomMode ? " (random mode — BPM not yet known)" : $" (±{bpmRange} BPM of {refBpm:F0})");
            return picks.Count;
        }
        finally { _mutateGate.Release(); }
    }

    /// <summary>Priority-weighted random draw: priority 1–5 is the weight, so a
    /// priority-5 playlist is drawn five times as often as a priority-1.</summary>
    private static SavedPlaylist WeightedPick(IReadOnlyList<SavedPlaylist> pls, Random rng)
    {
        int total = pls.Sum(p => Math.Clamp(p.Priority, 1, 5));
        int r = rng.Next(total);
        foreach (var p in pls)
        {
            r -= Math.Clamp(p.Priority, 1, 5);
            if (r < 0) return p;
        }
        return pls[^1];
    }

    /// <summary>Score-weighted random draw over the candidate tracks.</summary>
    private static Track WeightedPickTrack(List<(Track t, double s)> scored, Random rng)
    {
        double total = scored.Sum(x => Math.Max(0.001, x.s));
        double r = rng.NextDouble() * total;
        foreach (var (t, s) in scored)
        {
            r -= Math.Max(0.001, s);
            if (r < 0) return t;
        }
        return scored[^1].t;
    }

    /// <summary>True when any of the playlist's enabled slots covers the given
    /// moment. A playlist with no slots is never schedule-active.</summary>
    private static bool IsPlaylistActiveNow(SavedPlaylist pl, DateTime now)
    {
        if (pl.Slots.Count == 0) return false;
        int todayDow = ((int)now.DayOfWeek + 6) % 7; // Mon=0 … Sun=6
        TimeSpan nowTime = now.TimeOfDay;
        foreach (var slot in pl.Slots)
        {
            if (!slot.Enabled) continue;
            if (SlotCoversNow(slot, todayDow, nowTime)) return true;
        }
        return false;
    }

    private static bool SlotCoversNow(SavedPlaylistSlot slot, int todayDow, TimeSpan nowTime)
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
