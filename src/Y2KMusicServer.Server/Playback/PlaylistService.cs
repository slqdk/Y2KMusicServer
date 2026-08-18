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

    /// <summary>
    /// The next entry to play plus whether the current track was actually found
    /// in the queue. The scheduler needs both: when the current track IS in the
    /// queue, a next entry with the same TrackId is a genuine repeat and must be
    /// armed; when it is not (operator played something off-playlist), arming
    /// the same id again would replay what's already on the deck.
    /// </summary>
    public async Task<(int? TrackId, bool CurrentInQueue)> NextUpcomingAsync(int? currentTrackId, CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var entries = await db.PlaylistEntries.AsNoTracking()
            .OrderBy(e => e.Position).ToListAsync(ct);
        int curPos = CurrentPosition(entries, currentTrackId);
        var next = entries.Where(e => e.Position > curPos)
            .OrderBy(e => e.Position)
            .Select(e => (int?)e.TrackId)
            .FirstOrDefault();
        return (next, curPos >= 0);
    }

    // ── Live playlist selection (listener chips / DJ page) ────────────────────

    /// <summary>
    /// A selection change is pending; the scheduler performs the swap once this
    /// moment passes. Every further change pushes it out again, so a burst of
    /// tapping produces exactly one queue swap instead of one per tap.
    /// </summary>
    private DateTime _swapDueUtc = DateTime.MaxValue;
    private static readonly TimeSpan SwapDebounce = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Records which playlists Auto DJ should draw from and arms the debounced
    /// swap. An empty selection clears every override, handing control back to
    /// the timeslots. Non-empty means exactly those playlists: chosen ones are
    /// forced ON, all others forced OFF, so the clock cannot add anything else.
    /// </summary>
    public async Task SetLiveSelectionAsync(IReadOnlyCollection<int> playlistIds, CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var all = await db.SavedPlaylists.AsNoTracking().Select(p => p.Id).ToListAsync(ct);

        if (playlistIds.Count == 0)
        {
            AutoDjFeedStore.Clear(_cfg);
            _log.LogInformation("Live selection cleared — timeslots decide again; queue swap in {Sec}s.",
                SwapDebounce.TotalSeconds);
        }
        else
        {
            var chosen = playlistIds.ToHashSet();
            foreach (var id in all) AutoDjFeedStore.Set(_cfg, id, chosen.Contains(id));
            _log.LogInformation("Live selection: {Count} playlist(s) forced on, the rest off; queue swap in {Sec}s.",
                chosen.Count, SwapDebounce.TotalSeconds);
        }

        _swapDueUtc = DateTime.UtcNow + SwapDebounce;
    }

    /// <summary>The ids currently forced on (for the UI's chip state).</summary>
    public HashSet<int> LiveSelection() => AutoDjFeedStore.LoadState(_cfg).On;

    /// <summary>True when a debounced swap has come due (and consumes it).</summary>
    public bool TakeDueSwap()
    {
        if (_swapDueUtc == DateTime.MaxValue || DateTime.UtcNow < _swapDueUtc) return false;
        _swapDueUtc = DateTime.MaxValue;
        return true;
    }

    /// <summary>
    /// Clean sweep: drop EVERY upcoming entry — Auto DJ picks, activated
    /// playlist rows and outstanding requests alike — then refill from whatever
    /// is feeding now. The playing track and the played history stay. Returns
    /// how many were removed and how many were added.
    /// </summary>
    public async Task<(int Removed, int Added)> SwapQueueToActiveFeedsAsync(int? currentTrackId, CancellationToken ct = default)
    {
        int removed = await ClearUpcomingAsync(currentTrackId, ct);
        int added = await TopUpAsync(ct);
        _log.LogInformation("Queue swap: {Removed} upcoming entr(ies) cleared, {Added} added from the live selection.",
            removed, added);
        return (removed, added);
    }

    /// <summary>
    /// Removes every UPCOMING queue entry that came from a given saved playlist
    /// (matched on the Added-by label the top-up writes). The playing track and
    /// the played history are left alone, and positions are closed up
    /// afterwards. Returns how many were removed.
    /// </summary>
    public async Task<int> RemoveUpcomingBySourceAsync(string playlistName, CancellationToken ct = default)
    {
        await _mutateGate.WaitAsync(ct);
        try
        {
            await using var db = await _dbf.CreateDbContextAsync(ct);
            var entries = await db.PlaylistEntries.OrderBy(e => e.Position).ToListAsync(ct);
            if (entries.Count == 0) return 0;

            EnsurePlayheadLoaded();
            int head = Volatile.Read(ref _playheadEntryId);
            var cur = head != 0 ? entries.FirstOrDefault(e => e.Id == head) : null;
            int curPos = cur?.Position ?? -1;

            var doomed = entries
                .Where(e => e.Position > curPos
                            && string.Equals(e.AddedBy, playlistName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (doomed.Count == 0) return 0;

            db.PlaylistEntries.RemoveRange(doomed);
            await db.SaveChangesAsync(ct);
            await RenumberAsync(db, ct);
            return doomed.Count;
        }
        finally { _mutateGate.Release(); }
    }

    /// <summary>
    /// The saved playlists with their slots loaded — for callers that need to
    /// ask <see cref="IsPlaylistActiveNow"/> about each one (the DJ page).
    /// </summary>
    public async Task<IReadOnlyList<SavedPlaylist>> SavedPlaylistsWithSlotsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        return await db.SavedPlaylists.AsNoTracking()
            .Include(pl => pl.Slots)
            .Include(pl => pl.Tracks)
            .OrderBy(pl => pl.TileOrder)
            .ToListAsync(ct);
    }

    /// <summary>
    /// The entry id the queue has played up to (0 = none). Survives restarts,
    /// so the admin queue can still grey out what already played when nothing
    /// is on the deck.
    /// </summary>
    public int PlayedThroughEntryId()
    {
        EnsurePlayheadLoaded();
        return Volatile.Read(ref _playheadEntryId);
    }

    /// <summary>
    /// Where playback should start when nothing is loaded — after a restart, a
    /// power cut, or a plain Stop. The first entry AFTER the persisted playhead,
    /// so the rows already played are skipped; the queue head when there is no
    /// playhead (a fresh queue); null when the whole queue has been played
    /// (the caller can top up and ask again).
    /// </summary>
    public async Task<int?> ResumeTrackIdAsync(CancellationToken ct = default)
    {
        EnsurePlayheadLoaded();

        await using var db = await _dbf.CreateDbContextAsync(ct);
        var entries = await db.PlaylistEntries.AsNoTracking()
            .OrderBy(e => e.Position).ToListAsync(ct);
        if (entries.Count == 0) return null;

        int head = Volatile.Read(ref _playheadEntryId);
        var cur = head != 0 ? entries.FirstOrDefault(e => e.Id == head) : null;

        // The remembered entry is gone (pruned, or the queue was replaced) but
        // its track may still be in the list — resume after that copy instead of
        // replaying the whole queue.
        if (cur == null && head != 0)
        {
            int tid = Volatile.Read(ref _playheadTrackId);
            if (tid != 0) cur = entries.LastOrDefault(e => e.TrackId == tid);
        }

        if (cur == null) return entries[0].TrackId;   // nothing remembered → start at the top
        return entries.FirstOrDefault(e => e.Position > cur.Position)?.TrackId;
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
    /// Played entries kept visible above the current track (the operator's
    /// recent-history view); anything older is pruned. Three: enough to see
    /// what just played and undo a mistake, few enough that the queue table
    /// stays about the upcoming music rather than the evening's archive.
    /// </summary>
    public const int PlayedHistoryKeep = 3;

    /// <summary>
    /// Prunes consumed entries, but keeps the last <see cref="PlayedHistoryKeep"/>
    /// of them: the queue view is "recent history + now playing + upcoming", so
    /// the operator always sees what just played. No-op if the current track
    /// isn't in the playlist.
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

            var stale = entries.Where(e => e.Position < curPos - PlayedHistoryKeep).ToList();
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

    public sealed record ActivateOutcome(
        ActivateResult Result, int Added, int SkippedMissing, int SkippedDuplicate);

    /// <summary>
    /// Replaces the live queue with a saved playlist: upcoming entries are
    /// cleared EXCEPT pending Request entries (requests survive and play first),
    /// then the saved playlist's tracks are appended in order as Schedule
    /// entries labelled with the playlist's name. The currently playing track is
    /// untouched; the caller fires the crossfade into the first upcoming entry.
    /// Tracks whose file is missing, or that already sit in the kept portion,
    /// are skipped. Returns the count appended via <paramref name="added"/>.
    /// </summary>
    public async Task<ActivateOutcome> ActivateSavedAsync(
        int savedPlaylistId, int? currentTrackId, CancellationToken ct = default)
    {
        await _mutateGate.WaitAsync(ct);
        try
        {
            await using var db = await _dbf.CreateDbContextAsync(ct);

            var saved = await db.SavedPlaylists.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == savedPlaylistId, ct);
            if (saved == null) return new ActivateOutcome(ActivateResult.NotFound, 0, 0, 0);

            var savedTracks = await db.SavedPlaylistTracks.AsNoTracking()
                .Where(x => x.SavedPlaylistId == savedPlaylistId)
                .OrderBy(x => x.Position)
                .Select(x => new { x.TrackId, x.Track!.FilePath })
                .ToListAsync(ct);
            if (savedTracks.Count == 0) return new ActivateOutcome(ActivateResult.Empty, 0, 0, 0);

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

            int added = 0, missing = 0, dupes = 0;
            foreach (var s in savedTracks)
            {
                if (keptIds.Contains(s.TrackId)) { dupes++; continue; }
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

            return new ActivateOutcome(ActivateResult.Ok, added, missing, dupes);
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

    // Auto DJ duration policy: never auto-pick jingle-length or marathon
    // tracks. Applies ONLY to Auto DJ's own picks — manual adds, saved-playlist
    // activation, and listener requests are the operator's/listener's explicit
    // choice and are not gated.
    private const double MinAutoDjDurationSec = 90;   // 1:30
    private const double MaxAutoDjDurationSec = 360;  // 6:00

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
            // A playlist feeds the queue when the operator toggled it on
            // (autodj-feeds.json — the old "category enabled" switch) OR when one
            // of its enabled slots covers this moment. Union, so the clock keeps
            // working in the background while a manual toggle can force a
            // playlist in outside its window.
            // ON beats the clock, OFF beats both — see AutoDjFeedStore.
            var feeds = AutoDjFeedStore.LoadState(_cfg);
            var active = playlists
                .Where(pl => feeds.IsActive(pl, now, IsPlaylistActiveNow))
                .ToList();
            if (active.Count == 0)
            {
                _log.LogDebug("Auto DJ top-up skipped: no playlist is toggled on and no timeslot covers right now.");
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
                        if (t.DurationSec < MinAutoDjDurationSec || t.DurationSec > MaxAutoDjDurationSec) return false;
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
    /// <summary>
    /// True when one of the playlist's enabled slots covers <paramref name="now"/>.
    /// Public so the admin API can light the same lamp the top-up actually uses —
    /// one implementation, no drift between what the UI claims and what plays.
    /// </summary>
    public static bool IsPlaylistActiveNow(SavedPlaylist pl, DateTime now)
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
        if (!TimeSpan.TryParse(slot.TimeFromHHmm, out var from)) return false;
        if (!TimeSpan.TryParse(slot.TimeToHHmm, out var to)) return false;

        // "To" is minute-INCLUSIVE: 23:59 covers through 23:59:59. Without
        // this the seeded always-on slot (00:00–23:59) had a dead 59 s before
        // midnight — a top-up landing there found no active playlist.
        var toEnd = to.Add(TimeSpan.FromMinutes(1));

        // DaysMask: bit 0 = Monday … bit 6 = Sunday. 0 = every day.
        bool DayOk(int dow) => slot.DaysMask == 0 || (slot.DaysMask & (1 << dow)) != 0;

        if (from < toEnd)                                   // same-day range
            return DayOk(todayDow) && nowTime >= from && nowTime < toEnd;

        // Overnight wrap (e.g. Fri 22:00–02:00): the evening leg belongs to
        // the ticked day; the after-midnight tail belongs to the PREVIOUS
        // day's tick — a Friday party slot must still cover Saturday 01:00
        // without Saturday being ticked.
        int yesterdayDow = (todayDow + 6) % 7;
        return (DayOk(todayDow) && nowTime >= from)
            || (DayOk(yesterdayDow) && nowTime < toEnd);
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
    /// The entry the playhead is on. Tracked by ENTRY id, not track id: the same
    /// song may legitimately appear in the queue more than once (a request for
    /// something already scheduled, an activated playlist containing a repeat),
    /// and resolving the playhead by track id alone snapped it back to the FIRST
    /// copy — after which "everything after the current track" pointed at the
    /// wrong slice and playback looped over the songs between the two copies
    /// forever. Entry ids survive renumbering; positions don't.
    /// </summary>
    private int _playheadEntryId;   // 0 = none yet
    private int _playheadTrackId;
    private bool _playheadLoaded;

    /// <summary>
    /// Reads the persisted playhead once per process, so the first status tick
    /// after a restart already knows where the queue left off.
    /// </summary>
    private void EnsurePlayheadLoaded()
    {
        if (_playheadLoaded) return;
        _playheadLoaded = true;
        var p = PlayheadStore.Load(_cfg);
        if (p.EntryId > 0)
        {
            Volatile.Write(ref _playheadEntryId, p.EntryId);
            Volatile.Write(ref _playheadTrackId, p.TrackId);
            _log.LogInformation("Queue playhead restored: entry {EntryId} (track {TrackId}).", p.EntryId, p.TrackId);
        }
    }

    /// <summary>
    /// The Position of the current track in the (ordered) entry list, or -1 if
    /// it isn't present — in which case the whole list is "upcoming".
    /// </summary>
    private int CurrentPosition(List<PlaylistEntry> ordered, int? currentTrackId)
    {
        if (currentTrackId is not int id) return -1;
        EnsurePlayheadLoaded();

        // 1. Same entry as last time → nothing to re-resolve.
        int head = Volatile.Read(ref _playheadEntryId);
        if (head != 0)
        {
            var cur = ordered.FirstOrDefault(e => e.Id == head);
            if (cur != null && cur.TrackId == id) return cur.Position;
        }

        // 2. New track: adopt the first copy AFTER the previous playhead, so a
        //    duplicate earlier in the queue can never drag the playhead back.
        int minPos = -1;
        if (head != 0)
        {
            var prev = ordered.FirstOrDefault(e => e.Id == head);
            if (prev != null) minPos = prev.Position;
        }
        var match = ordered.FirstOrDefault(e => e.TrackId == id && e.Position > minPos)
                    ?? ordered.FirstOrDefault(e => e.TrackId == id);
        if (match == null) return -1;

        Volatile.Write(ref _playheadEntryId, match.Id);
        Volatile.Write(ref _playheadTrackId, match.TrackId);
        PlayheadStore.Save(_cfg, match.Id, match.TrackId);   // survives a restart / power cut
        return match.Position;
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
