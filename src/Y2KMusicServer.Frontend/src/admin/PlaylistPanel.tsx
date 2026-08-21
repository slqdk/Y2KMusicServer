import { useEffect, useRef, useState, type MouseEvent as ReactMouseEvent } from 'react'
import * as api from './api'
import { fmtTime } from './api'
import SlotsDialog from './SlotsDialog'
import { useColumnWidths, ColResizer } from './useColumns'

// Right-click menu geometry, used only to keep menus inside the viewport.
const MENU_W = 200
const ROW_MENU_H = 84
const TILE_MENU_H = 236

type RowMenu = { x: number; y: number; entry: api.PlaylistItem }
type TileMenu = { x: number; y: number; pl: api.SavedPlaylistDto }

export default function PlaylistPanel(
  { onPlayNow, nowPlayingTrackId }:
  { onPlayNow: (trackId: number) => Promise<unknown> | void; nowPlayingTrackId: number | null }
) {
  const [list, setList] = useState<api.PlaylistItem[]>([])
  const [busy, setBusy] = useState(false)
  const [selId, setSelId] = useState<number | null>(null)
  const [menu, setMenu] = useState<RowMenu | null>(null)
  const nowRowRef = useRef<HTMLTableRowElement | null>(null)

  // Saved playlists: the tile strip, and the "viewing" mode that swaps the
  // live queue for a saved playlist's content until Back is pressed.
  const [tiles, setTiles] = useState<api.SavedPlaylistDto[]>([])
  const [maxTiles, setMaxTiles] = useState(14)
  const [viewing, setViewing] = useState<api.SavedPlaylistDto | null>(null)
  const [viewItems, setViewItems] = useState<api.SavedPlaylistTrackDto[]>([])
  const [tileMenu, setTileMenu] = useState<TileMenu | null>(null)
  const [naming, setNaming] = useState(false)          // the New-playlist tile is an input
  const [newName, setNewName] = useState('')
  const [renaming, setRenaming] = useState<api.SavedPlaylistDto | null>(null)
  const [renameVal, setRenameVal] = useState('')
  const [confirmDel, setConfirmDel] = useState<api.SavedPlaylistDto | null>(null)
  const [schedFor, setSchedFor] = useState<api.SavedPlaylistDto | null>(null)
  const [autoDj, setAutoDj] = useState<boolean | null>(null)
  const [note, setNote] = useState<string | null>(null)

  // Resizable, fixed-width columns: #, Title, Artist, Dur, Mix-in, BPM, LUFS,
  // Added by, and the remove button.
  const { colgroup, startResize } = useColumnWidths('y2k.cols.playlist', [5, 25, 22, 8, 9, 7, 8, 11, 5])
  // The saved-playlist view has no Mix-in / Added-by: #, Title, Artist, Type, Dur, BPM, LUFS, ✕.
  // NINE widths for NINE columns: the leading checkbox counts. With
  // table-layout:fixed every <col> is positional, so a short list silently
  // shifts each width one column left — which is what made the select column
  // eat the width meant for "#". The stored array is length-checked against the
  // defaults, so an old 8-wide entry in localStorage is ignored, not misapplied.
  const view = useColumnWidths('y2k.cols.savedlist', [3, 5, 29, 24, 7, 8, 8, 9, 5])

  const [reqs, setReqs] = useState<api.RequestDto[]>([])
  // The queue plus the persisted playhead: after a restart nothing is playing,
  // so "already played" can only come from the playhead the server remembers.
  const [playedThrough, setPlayedThrough] = useState(0)
  const refreshList = () =>
    api.getPlaylistState()
      .then(s => { setList(s.items); setPlayedThrough(s.playedThroughEntryId) })
      .catch(() => {})
  const refreshReqs = () => api.getRequests().then(setReqs).catch(() => {})
  const refreshTiles = () =>
    api.getSavedPlaylists().then(r => { setTiles(r.playlists); setMaxTiles(r.max) }).catch(() => {})
  const refreshView = (pl: api.SavedPlaylistDto) =>
    api.getSavedPlaylistTracks(pl.id).then(r => setViewItems(r.items)).catch(() => setViewItems([]))

  const refreshAutoDj = () => api.getAutoDj().then(s => setAutoDj(s.autoDj)).catch(() => {})
  useEffect(() => {
    refreshList(); refreshTiles(); refreshAutoDj(); refreshReqs()
    const id = setInterval(() => { refreshList(); refreshTiles(); refreshReqs() }, 2000) // surfaces Auto DJ top-ups + adds + requests
    return () => clearInterval(id)
  }, [])

  const toggleAutoDj = () => guard(async () => {
    if (autoDj == null) return
    const r = await api.setAutoDj({ on: !autoDj })
    setAutoDj(r.autoDj)
  })

  // Keep the open saved-playlist view fresh (right-click adds from the library).
  useEffect(() => {
    if (!viewing) return
    refreshView(viewing)
    const id = setInterval(() => refreshView(viewing), 2000)
    return () => clearInterval(id)
  }, [viewing?.id]) // eslint-disable-line react-hooks/exhaustive-deps

  // Dismiss any context menu on click, scroll, resize, or Escape. The menu
  // item's own onClick bubbles to the React root before this window listener,
  // so the action still fires.
  useEffect(() => {
    if (!menu && !tileMenu) return
    const close = () => { setMenu(null); setTileMenu(null) }
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') close() }
    window.addEventListener('click', close)
    window.addEventListener('resize', close)
    window.addEventListener('scroll', close, true)
    window.addEventListener('keydown', onKey)
    return () => {
      window.removeEventListener('click', close)
      window.removeEventListener('resize', close)
      window.removeEventListener('scroll', close, true)
      window.removeEventListener('keydown', onKey)
    }
  }, [menu, tileMenu])

  // Keep the playing row vertically centred so a couple of just-played songs
  // stay visible above it and the upcoming ones below. Re-centres on track
  // change only, so the operator can still scroll around freely in between.
  useEffect(() => {
    if (nowPlayingTrackId == null) return
    const id = window.setTimeout(() =>
      nowRowRef.current?.scrollIntoView({ block: 'center', behavior: 'smooth' }), 150)
    return () => window.clearTimeout(id)
  }, [nowPlayingTrackId])

  useEffect(() => {
    if (!note) return
    const id = window.setTimeout(() => setNote(null), 3000)
    return () => window.clearTimeout(id)
  }, [note])

  const guard = async (fn: () => Promise<unknown>) => {
    setBusy(true)
    try { await fn() } catch { /* ignore */ } finally { setBusy(false) }
  }

  // Export downloads via a transient anchor (Content-Disposition names the
  // file); import reads a chosen JSON file and posts it. Results land in the
  // same note line the other tile actions use.
  const importFileRef = useRef<HTMLInputElement | null>(null)
  const exportPlaylist = (pl: api.SavedPlaylistDto) => {
    const a = document.createElement('a')
    a.href = api.exportSavedPlaylistUrl(pl.id)
    a.download = ''
    document.body.appendChild(a)
    a.click()
    a.remove()
  }
  const importPlaylistFile = async (file: File) => {
    try {
      const text = await file.text()
      let payload: unknown
      try { payload = JSON.parse(text) } catch { setNote('That file is not a playlist export (invalid JSON).'); return }
      const r = await api.importSavedPlaylist(payload)
      setNote(r.missing > 0
        ? `Imported “${r.name}”: ${r.matched} track(s) matched, ${r.missing} not in the library` +
          (r.missingSamples.length > 0 ? ` (e.g. ${r.missingSamples[0]})` : '') + '.'
        : `Imported “${r.name}”: all ${r.matched} track(s) matched.`)
      await refreshTiles()
    } catch (e) {
      setNote(e instanceof api.ApiError ? e.message : 'Import failed.')
    }
  }

  // Auto DJ feed toggle — the old category switch. A playlist feeds the queue
  // when it's toggled on OR when one of its timeslots covers right now, so the
  // tile shows both states.
  // Switching a playlist off stops future picks; this clears what Auto DJ
  // already queued from it (upcoming rows only — never the playing track).
  const purgeQueued = (pl: api.SavedPlaylistDto) =>
    guard(async () => {
      const r = await api.purgeQueuedFromPlaylist(pl.id)
      setNote(r.removed === 0
        ? `Nothing queued from “${r.playlist}”.`
        : `Removed ${r.removed} queued track(s) from “${r.playlist}”.`)
      await refreshList()
    })

  // Three states, cycled in the order they're most often wanted:
  //   on (✓)  →  off (✕)  →  schedule (⏱)  →  on …
  // The schedule state is the neutral one — no override stored — and it is the
  // ONLY state in which a playlist's timeslots have any effect. Without it in
  // the cycle, Schedule… was dead for every playlist ever clicked.
  const nextFeedState = (pl: api.SavedPlaylistDto): boolean | null =>
    pl.explicitOn ? false : pl.forcedOff ? null : true

  const toggleFeed = (pl: api.SavedPlaylistDto) =>
    guard(async () => { await api.setPlaylistFeed(pl.id, nextFeedState(pl)); await refreshTiles() })

  const setFeedState = (pl: api.SavedPlaylistDto, value: boolean | null) =>
    guard(async () => { await api.setPlaylistFeed(pl.id, value); await refreshTiles() })

  // Multi-select inside a saved playlist, for moving tracks between playlists.
  // Keyed on entryId (a track can sit in several playlists, entries can't).
  const [sel, setSel] = useState<Set<number>>(new Set())
  const [moveTo, setMoveTo] = useState<number | ''>('')
  const toggleSel = (entryId: number) =>
    setSel(prev => {
      const n = new Set(prev)
      if (!n.delete(entryId)) n.add(entryId)
      return n
    })
  const selectAllView = () =>
    setSel(prev => prev.size === viewItems.length
      ? new Set()
      : new Set(viewItems.map(t => t.entryId)))
  // Leaving the playlist (or reloading it) must not keep a stale selection.
  useEffect(() => { setSel(new Set()); setMoveTo('') }, [viewing?.id])

  const moveSelected = (copy: boolean) => {
    if (!viewing || sel.size === 0 || moveTo === '') return
    guard(async () => {
      const r = await api.moveSavedPlaylistTracks(viewing.id, [...sel], Number(moveTo), copy)
      setNote(`${copy ? 'Copied' : 'Moved'} ${r.added} track(s) to “${r.target}”`
        + (r.skipped > 0 ? ` — ${r.skipped} already there` : '') + '.')
      setSel(new Set())
      await refreshView(viewing)
      await refreshTiles()
    })
  }

  // Designating a playlist as the jingle playlist also takes it out of Auto DJ
  // and off the guest page — both enforced server-side, so the refresh below is
  // what makes the tile tell the truth.
  // Fire a jingle: cue on Deck B and crossfade immediately. Same call the phone
  // makes, so both consoles behave identically.
  const fire = (trackId: number, title: string | null) =>
    guard(async () => {
      await api.fireJingle(trackId)
      setNote(`Firing “${title ?? 'jingle'}”…`)
    })

  const toggleJingles = (pl: api.SavedPlaylistDto) =>
    guard(async () => {
      await api.setPlaylistJingles(pl.id, !pl.isJingle)
      await refreshTiles()
      setNote(pl.isJingle
        ? `“${pl.name}” is a normal playlist again.`
        : `“${pl.name}” is now the jingle playlist — fire its tracks from here or /DJAdmin.`)
    })

  const acceptReq = (id: number) =>
    guard(async () => { await api.acceptRequest(id); await refreshReqs(); await refreshList() })
  const dismissReq = (id: number) =>
    guard(async () => { await api.dismissRequest(id); await refreshReqs() })

  const remove = (id: number) =>
    guard(async () => { await api.removeEntry(id); await refreshList() })

  // Crossfade to this entry now (the parent owns the decision — it has the live
  // playback status), then drop the entry from the queue so the auto-advance
  // doesn't play it again.
  const playNowEntry = (e: api.PlaylistItem) => guard(async () => {
    await onPlayNow(e.trackId)
    await api.removeEntry(e.id)
    await refreshList()
  })

  const openMenu = (ev: ReactMouseEvent, e: api.PlaylistItem) => {
    ev.preventDefault()
    setSelId(e.id)
    const x = Math.max(4, Math.min(ev.clientX, window.innerWidth - MENU_W - 4))
    const y = Math.max(4, Math.min(ev.clientY, window.innerHeight - ROW_MENU_H - 4))
    setMenu({ x, y, entry: e })
  }

  // ── Saved-playlist actions ────────────────────────────────────────────────

  const openTileMenu = (ev: ReactMouseEvent, pl: api.SavedPlaylistDto) => {
    ev.preventDefault()
    const x = Math.max(4, Math.min(ev.clientX, window.innerWidth - MENU_W - 4))
    const y = Math.max(4, Math.min(ev.clientY, window.innerHeight - TILE_MENU_H - 4))
    setTileMenu({ x, y, pl })
  }

  const create = () => {
    const name = newName.trim()
    if (!name) { setNaming(false); setNewName(''); return }
    guard(async () => {
      try {
        await api.createSavedPlaylist(name)
        setNaming(false); setNewName('')
        await refreshTiles()
      } catch (e) {
        setNote(e instanceof api.ApiError ? e.message : 'Could not create the playlist.')
      }
    })
  }

  const doRename = () => {
    if (!renaming) return
    const name = renameVal.trim()
    if (!name) { setRenaming(null); return }
    guard(async () => {
      try {
        await api.renameSavedPlaylist(renaming.id, name)
        setRenaming(null)
        await refreshTiles()
        if (viewing?.id === renaming.id) setViewing({ ...viewing, name })
      } catch (e) {
        setNote(e instanceof api.ApiError ? e.message : 'Rename failed.')
      }
    })
  }

  const doDelete = (pl: api.SavedPlaylistDto) => guard(async () => {
    await api.deleteSavedPlaylist(pl.id)
    setConfirmDel(null)
    if (viewing?.id === pl.id) setViewing(null)
    await refreshTiles()
  })

  const setPriority = (pl: api.SavedPlaylistDto, v: number) => guard(async () => {
    await api.setSavedPlaylistPriority(pl.id, v)
    await refreshTiles()
  })

  const activate = (pl: api.SavedPlaylistDto) => guard(async () => {
    try {
      const r = await api.activateSavedPlaylist(pl.id)
      const extras = [
        r.skippedAlreadyQueued > 0 ? `${r.skippedAlreadyQueued} already in the queue` : '',
        r.skippedMissing > 0 ? `${r.skippedMissing} skipped (file missing)` : ''
      ].filter(Boolean).join(', ')
      const detail = `${r.queued} queued${extras ? ` — ${extras}` : ''}`
      setNote(r.action === 'crossfaded' ? `"${pl.name}" is live (${detail}) — crossfading.`
        : r.action === 'started' ? `"${pl.name}" is live (${detail}) — playback started.`
        : `"${pl.name}": ${detail}.`)
      setViewing(null)
      await refreshList()
    } catch (e) {
      setNote(e instanceof api.ApiError ? e.message : 'Activate failed.')
    }
  })

  const removeViewTrack = (entryId: number) => guard(async () => {
    if (!viewing) return
    await api.removeSavedPlaylistTrack(viewing.id, entryId)
    await refreshView(viewing)
    await refreshTiles()
  })

  return (
    <div className="w-panel w-raised w-playlistpanel">
      <div className="w-panelhead" style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
        <span>Playlist</span>
        <span style={{ flex: 1 }} />
        <button className="w-btn" disabled={busy} title="Import a playlist from an exported .y2kpl.json file"
          onClick={() => importFileRef.current?.click()}>Import…</button>
        <input ref={importFileRef} type="file" accept=".json,application/json" style={{ display: 'none' }}
          onChange={e => {
            const f = e.target.files?.[0]
            e.target.value = ''
            if (f) void importPlaylistFile(f)
          }} />
        <button className={`w-btn ${autoDj ? 'w-autodj-on' : ''}`} disabled={busy || autoDj == null}
          title="Auto DJ fills the queue from the saved playlists whose timeslot is active (weighted by priority)"
          onClick={toggleAutoDj}>
          Auto DJ: {autoDj == null ? '…' : autoDj ? 'ON' : 'OFF'}
        </button>
      </div>

      {/* Saved-playlist tiles. Click = view; right-click = rename / delete /
          priority / activate; the last free slot is the New-playlist tile. */}
      <div className="w-pltiles">
        {/* The jingle playlist always renders last: it isn't part of the
            rotation, so it shouldn't sit among the playlists that are. */}
        {[...tiles].sort((a, b) => Number(a.isJingle) - Number(b.isJingle)).map(pl => (
          <div key={pl.id}
            className={'w-pltile w-raised'
              + (viewing?.id === pl.id ? ' w-viewing' : '')
              + (pl.isJingle ? ' w-pltile-jingle'
                : pl.explicitOn ? ' w-pltile-on'
                  : pl.forcedOff ? ' w-pltile-off' : ' w-pltile-sched')}
            onClick={() => setViewing(v => v?.id === pl.id ? null : pl)}
            onContextMenu={e => openTileMenu(e, pl)}
            style={{ position: 'relative' }}
            title={`Priority ${pl.priority} · ${pl.slotCount} timeslot(s)`
              + (pl.feed ? ' · Auto DJ: ON' : (pl.scheduledNow ? ' · Auto DJ: ON (timeslot)' : ' · Auto DJ: off'))}>
            <button className="w-btn w-tilemenu-btn" title="Playlist actions (Auto DJ / activate / schedule / rename / export / delete / priority)"
              onClick={e => { e.stopPropagation(); openTileMenu(e, pl) }}>▾</button>
            <div className="w-cat-name">{pl.name}</div>
            <div className="w-cat-count">{pl.trackCount} tracks</div>
            {/* The jingle playlist has no Auto DJ button at all: it is excluded
                server-side, so a button here could only lie about it. */}
            {pl.isJingle ? (
              <div className="w-tilejingle" title="Jingles — fired by hand from here or the DJ page. Never Auto DJ, never shown to guests.">
                🔔 Jingles
              </div>
            ) : (
            <button
              className={'w-btn w-tilefeed'
                + (pl.explicitOn ? ' w-tilefeed-on'
                  : pl.forcedOff ? ' w-tilefeed-off'
                    : pl.feed ? ' w-tilefeed-on w-tilefeed-sched' : ' w-tilefeed-sched')}
              disabled={busy}
              title={pl.explicitOn
                ? 'On: feeding Auto DJ whatever the schedule says — click for Off'
                : pl.forcedOff
                  ? 'Off: not feeding, and timeslots are ignored — click to follow the schedule'
                  : pl.feed
                    ? 'Schedule: a timeslot covers now, so it is feeding — click for On'
                    : 'Schedule: no timeslot covers now, so it is not feeding — click for On'}
              onClick={e => { e.stopPropagation(); toggleFeed(pl) }}>
              {/* Words, not just a glyph: at 10px the icons were the only
                  difference between two states and it did not read. */}
              {pl.explicitOn ? 'Auto DJ: ON'
                : pl.forcedOff ? 'Auto DJ: OFF'
                  : pl.feed ? 'SCHEDULE ⏱ on now' : 'SCHEDULE ⏱'}
            </button>
            )}
          </div>
        ))}
        {tiles.length < maxTiles && !naming && (
          <div className="w-pltile w-newtile w-raised" title="Create a new playlist"
            onClick={() => { setNaming(true); setNewName('') }}>
            <div className="w-cat-name">+ New</div>
            <div className="w-cat-count">playlist</div>
          </div>
        )}
        {naming && (
          <div className="w-pltile w-raised">
            <input type="text" autoFocus value={newName} style={{ width: '100%' }}
              onChange={e => setNewName(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter') create(); if (e.key === 'Escape') { setNaming(false); setNewName('') } }}
              onBlur={create}
              placeholder="Name…" />
          </div>
        )}
      </div>
      {note && <div className="w-muted" style={{ marginBottom: 4 }}>{note}</div>}

      {viewing ? (
        <>
          {/* VIEWING mode: a saved playlist's content, visually distinct from
              the live queue (amber header + explicit way back). */}
          <div className="w-viewhead">
            <span>VIEWING: {viewing.name} — saved playlist</span>
            <span style={{ flex: 1 }} />
            <label title="Auto DJ feed weight: a 5 feeds five times as often as a 1">Prio:{' '}
              <select value={viewing.priority}
                onChange={e => {
                  const v = Number(e.target.value)
                  setPriority(viewing, v)
                  setViewing({ ...viewing, priority: v })
                }}>
                {[1, 2, 3, 4, 5].map(v => <option key={v} value={v}>{v}</option>)}
              </select>
            </label>
            <button className="w-btn" title="When Auto DJ may feed from this playlist (day/time slots)"
              onClick={() => setSchedFor(viewing)}>Schedule…</button>
            <button className="w-btn" onClick={() => { setRenaming(viewing); setRenameVal(viewing.name) }}>Rename…</button>
            <button className="w-btn" title="Download this playlist as a portable file" onClick={() => exportPlaylist(viewing)}>Export…</button>
            <button className="w-btn" onClick={() => setConfirmDel(viewing)}>Delete…</button>
            <button className="w-btn" disabled={busy}
              title="Replace the live queue with this playlist (requests stay first) and crossfade into it"
              onClick={() => activate(viewing)}>▶ Activate</button>
            <button className="w-btn" onClick={() => setViewing(null)}>Back to live queue</button>
          </div>
          <div className="w-toolbar" style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            <button className="w-btn" disabled={busy || viewItems.length === 0} onClick={selectAllView}>
              {sel.size === viewItems.length && viewItems.length > 0 ? 'Select none' : 'Select all'}
            </button>
            <span className="w-muted">{sel.size} selected</span>
            <span style={{ flex: 1 }} />
            <label>To:{' '}
              <select value={moveTo} disabled={busy || sel.size === 0}
                onChange={e => setMoveTo(e.target.value === '' ? '' : Number(e.target.value))}>
                <option value="">— choose playlist —</option>
                {tiles.filter(p => p.id !== viewing.id).map(p =>
                  <option key={p.id} value={p.id}>{p.name}</option>)}
              </select>
            </label>
            <button className="w-btn" disabled={busy || sel.size === 0 || moveTo === ''}
              title="Move the selected tracks out of this playlist and into the chosen one"
              onClick={() => moveSelected(false)}>Move →</button>
            <button className="w-btn" disabled={busy || sel.size === 0 || moveTo === ''}
              title="Add the selected tracks to the chosen playlist, keeping them here too"
              onClick={() => moveSelected(true)}>Copy →</button>
          </div>
          <div className="w-listwrap w-sunken" style={{ flex: 1, minHeight: 0, overflowX: 'hidden' }}>
            <table className="w-table w-grid">
              {view.colgroup}
              <thead>
                <tr>
                  <th className="w-selcol" title="Select tracks to move or copy">
                    <input type="checkbox" checked={sel.size > 0 && sel.size === viewItems.length}
                      onChange={selectAllView} disabled={viewItems.length === 0} />
                  </th>
                  <th className="w-num">#<ColResizer onMouseDown={view.startResize(1)} /></th>
                  <th>Title<ColResizer onMouseDown={view.startResize(2)} /></th>
                  <th>Artist<ColResizer onMouseDown={view.startResize(3)} /></th>
                  <th>Type<ColResizer onMouseDown={view.startResize(4)} /></th>
                  <th className="w-num">Dur<ColResizer onMouseDown={view.startResize(5)} /></th>
                  <th className="w-num">BPM<ColResizer onMouseDown={view.startResize(6)} /></th>
                  <th className="w-num">LUFS<ColResizer onMouseDown={view.startResize(7)} /></th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                {viewItems.map(t => (
                  <tr key={t.entryId} className={sel.has(t.entryId) ? 'w-rowsel' : ''}
                    onClick={() => toggleSel(t.entryId)}>
                    <td className="w-selcol" onClick={e => e.stopPropagation()}>
                      <input type="checkbox" checked={sel.has(t.entryId)}
                        onChange={() => toggleSel(t.entryId)} />
                    </td>
                    <td className="w-num">{t.position + 1}</td>
                    <td title={t.title ?? ''}>{t.title ?? '(untitled)'}</td>
                    <td title={t.artist ?? ''}>{t.artist ?? '---'}</td>
                    <td>{t.type ?? '---'}</td>
                    <td className="w-num">{fmtTime(t.durationSec)}</td>
                    <td className="w-num">{t.bpm != null ? Math.round(t.bpm) : '---'}</td>
                    <td className="w-num">{t.lufs != null ? t.lufs.toFixed(1) : '---'}</td>
                    <td className="w-rowbtns">
                      {viewing?.isJingle && (
                        <button className="w-btn w-firejingle" disabled={busy}
                          title="Fire this jingle now — crossfades straight into it"
                          onClick={e => { e.stopPropagation(); fire(t.trackId, t.title) }}>▶</button>
                      )}
                      <button className="w-btn" disabled={busy} title="Remove from this playlist"
                        onClick={() => removeViewTrack(t.entryId)}>✕</button>
                    </td>
                  </tr>
                ))}
                {viewItems.length === 0 && (
                  <tr><td colSpan={9} className="w-muted" style={{ padding: 8 }}>
                    Empty. Add tracks from the Library, or select tracks in another playlist and use “Move →”.
                  </td></tr>
                )}
              </tbody>
            </table>
          </div>
        </>
      ) : (
        <>
          {/* Live queue (now playing + upcoming); pending requests render as
              rows at the bottom of this same table, with their Accept/Dismiss
              buttons on the line. */}
          {/* Click a row to select; double-click plays it now (crossfade);
              right-click for the action menu. */}
          <div className="w-listwrap w-sunken" style={{ flex: 1, minHeight: 0, overflowX: 'hidden' }}>
            <table className="w-table w-grid">
              {colgroup}
              <thead>
                <tr>
                  <th className="w-num">#<ColResizer onMouseDown={startResize(0)} /></th>
                  <th>Title<ColResizer onMouseDown={startResize(1)} /></th>
                  <th>Artist<ColResizer onMouseDown={startResize(2)} /></th>
                  <th className="w-num">Dur<ColResizer onMouseDown={startResize(3)} /></th>
                  <th className="w-num">Mix-in<ColResizer onMouseDown={startResize(4)} /></th>
                  <th className="w-num">BPM<ColResizer onMouseDown={startResize(5)} /></th>
                  <th className="w-num">LUFS<ColResizer onMouseDown={startResize(6)} /></th>
                  <th>Added by<ColResizer onMouseDown={startResize(7)} /></th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                {(() => {
                  // First entry matching the playing track = "now" row; rows
                  // before it are played history (they're retained server-side).
                  // The playhead entry is the anchor — by ENTRY id, so the same
                  // song appearing twice can't drag the marker back to the first
                  // copy. Green only while that entry is actually on air; with
                  // the deck stopped it still marks how far the queue got.
                  const headIdx = playedThrough > 0
                    ? list.findIndex(x => x.id === playedThrough) : -1
                  const nowIdx = nowPlayingTrackId != null && headIdx >= 0
                      && list[headIdx].trackId === nowPlayingTrackId
                    ? headIdx
                    : nowPlayingTrackId == null
                      ? -1
                      : list.findIndex(x => x.trackId === nowPlayingTrackId)
                  // Rows up to and including the playhead are history when the
                  // deck is idle; while playing, history stops before the green row.
                  const playedIdx = nowIdx >= 0 ? nowIdx - 1 : headIdx
                  return list.map((e, i) => (
                  <tr key={e.id}
                    ref={i === nowIdx ? nowRowRef : undefined}
                    className={[
                      selId === e.id ? 'w-rowsel' : '',
                      i === nowIdx ? 'w-rownow' : i <= playedIdx ? 'w-rowplayed' : ''
                    ].filter(Boolean).join(' ')}
                    onClick={() => setSelId(e.id)}
                    onDoubleClick={() => playNowEntry(e)}
                    onContextMenu={ev => openMenu(ev, e)}
                    title={i === nowIdx ? 'Now playing'
                      : i <= playedIdx ? 'Already played'
                      : 'Double-click to play now (crossfade) · right-click for more'}>
                    <td className="w-num">{e.position + 1}</td>
                    <td title={e.title ?? ''}>{e.title ?? '(untitled)'}</td>
                    <td title={e.artist ?? ''}>{e.artist ?? '---'}</td>
                    <td className="w-num">{fmtTime(e.durationSec)}</td>
                    <td className="w-num">{e.introEndSec != null ? fmtTime(e.introEndSec) : '—'}</td>
                    <td className="w-num">{e.bpm != null ? Math.round(e.bpm) : '---'}</td>
                    <td className="w-num">{e.lufs != null ? e.lufs.toFixed(1) : '---'}</td>
                    <td><span className="w-srcbadge">{e.addedBy ?? e.source}</span></td>
                    <td className="w-rowbtns">
                      <button className="w-btn" disabled={busy} title="Remove"
                        onClick={ev => { ev.stopPropagation(); remove(e.id) }}>✕</button>
                    </td>
                  </tr>
                  ))
                })()}
                {reqs.filter(r => r.status === 'Pending').map(r => (
                  <tr key={`req-${r.id}`} className="w-rowreq" style={{ opacity: .75, fontStyle: 'italic' }}
                    title={`Requested by ${r.requesterName ?? 'unknown'} — accept to add to the queue`}>
                    <td className="w-num">?</td>
                    <td title={r.title ?? ''}>{r.title ?? '(untitled)'}</td>
                    <td title={r.artist ?? ''}>{r.artist ?? '---'}</td>
                    <td className="w-num">{fmtTime(r.durationSec)}</td>
                    <td className="w-num">—</td>
                    <td className="w-num">{r.bpm != null ? Math.round(r.bpm) : '---'}</td>
                    <td className="w-num">{r.lufs != null ? r.lufs.toFixed(1) : '---'}</td>
                    <td><span className="w-srcbadge">{r.requesterName ?? '—'} · Pending</span></td>
                    <td className="w-rowbtns" style={{ whiteSpace: 'nowrap' }}>
                      <button className="w-btn" disabled={busy} title="Accept → add to the queue"
                        onClick={ev => { ev.stopPropagation(); acceptReq(r.id) }}>✓</button>
                      <button className="w-btn" disabled={busy} title="Dismiss this request"
                        onClick={ev => { ev.stopPropagation(); dismissReq(r.id) }}>✕</button>
                    </td>
                  </tr>
                ))}
                {list.length === 0 && reqs.filter(r => r.status === 'Pending').length === 0 && (
                  <tr><td colSpan={9} className="w-muted" style={{ padding: 8 }}>Queue empty. Add tracks, activate a playlist, or enable Auto DJ.</td></tr>
                )}
              </tbody>
            </table>
          </div>
          <div className="w-toolbar">
            <button className="w-btn" disabled={busy || list.length === 0}
              onClick={() => guard(async () => { await api.clearPlaylist(); await refreshList() })}>Clear</button>
          </div>
        </>
      )}

      {menu && (
        <ul className="w-ctxmenu" role="menu" style={{ left: menu.x, top: menu.y }}
          onContextMenu={e => e.preventDefault()}>
          <li className="w-ctxitem" role="menuitem"
            onClick={() => { playNowEntry(menu.entry); setMenu(null) }}>Play now (crossfade)</li>
          <li className="w-ctxitem" role="menuitem"
            onClick={() => { remove(menu.entry.id); setMenu(null) }}>Remove from queue</li>
        </ul>
      )}

      {tileMenu && (
        <ul className="w-ctxmenu" role="menu" style={{ left: tileMenu.x, top: tileMenu.y, minWidth: MENU_W }}
          onContextMenu={e => e.preventDefault()}>
          {/* The three Auto DJ states, named rather than cycled — the tile button
              cycles for speed, the menu is where you go to be sure. */}
          <li className="w-ctxitem" role="menuitem"
            title="Feed Auto DJ from this playlist regardless of its timeslots"
            onClick={() => { setFeedState(tileMenu.pl, true); setTileMenu(null) }}>
            {tileMenu.pl.explicitOn ? '✓ ' : ''}Auto DJ uses this playlist
          </li>
          <li className="w-ctxitem" role="menuitem"
            title="Never feed Auto DJ from this playlist — timeslots are ignored while it is off"
            onClick={() => { setFeedState(tileMenu.pl, false); setTileMenu(null) }}>
            {tileMenu.pl.forcedOff ? '✓ ' : ''}Auto DJ never uses this playlist
          </li>
          <li className="w-ctxitem" role="menuitem"
            title="Follow this playlist's timeslots — the only state in which Schedule… has any effect"
            onClick={() => { setFeedState(tileMenu.pl, null); setTileMenu(null) }}>
            {!tileMenu.pl.explicitOn && !tileMenu.pl.forcedOff ? '✓ ' : ''}⏱ Auto DJ follows the schedule
          </li>
          <li className="w-ctxitem" role="menuitem"
            title="Reserve this playlist for hand-fired jingles: out of Auto DJ, off the guest page, fired from here or /DJAdmin"
            onClick={() => { toggleJingles(tileMenu.pl); setTileMenu(null) }}>
            {tileMenu.pl.isJingle ? '✓ Jingle playlist (click to release)' : '🔔 Use as the jingle playlist'}
          </li>
          <li className="w-ctxitem" role="menuitem"
            onClick={() => { activate(tileMenu.pl); setTileMenu(null) }}>▶ Activate (replace queue now)</li>
          <li className="w-ctxitem" role="menuitem"
            onClick={() => { setSchedFor(tileMenu.pl); setTileMenu(null) }}>Schedule…</li>
          <li className="w-ctxitem" role="menuitem"
            onClick={() => { setRenaming(tileMenu.pl); setRenameVal(tileMenu.pl.name); setTileMenu(null) }}>Rename…</li>
          <li className="w-ctxitem" role="menuitem"
            onClick={() => { exportPlaylist(tileMenu.pl); setTileMenu(null) }}>Export…</li>
          <li className="w-ctxitem" role="menuitem"
            title="Drop this playlist's not-yet-played tracks from the live queue"
            onClick={() => { purgeQueued(tileMenu.pl); setTileMenu(null) }}>Remove queued tracks…</li>
          <li className="w-ctxitem" role="menuitem"
            onClick={() => { setConfirmDel(tileMenu.pl); setTileMenu(null) }}>Delete…</li>
          <li className="w-ctxsep" role="separator" />
          <li className="w-ctxhead" aria-hidden="true">Priority (Auto DJ weight)</li>
          {[1, 2, 3, 4, 5].map(v => (
            <li key={v} className="w-ctxitem" role="menuitem" style={{ paddingLeft: 18 }}
              onClick={() => { setPriority(tileMenu.pl, v); setTileMenu(null) }}>
              {v}{tileMenu.pl.priority === v ? ' ●' : ''}
            </li>
          ))}
        </ul>
      )}

      {schedFor && (
        <SlotsDialog playlist={schedFor} onClose={() => setSchedFor(null)} onChanged={refreshTiles} />
      )}

      {renaming && (
        <div className="w-overlay" onMouseDown={() => setRenaming(null)}>
          <div className="w-dialog w-raised" onMouseDown={e => e.stopPropagation()} style={{ width: 300 }}>
            <div className="w-titlebar"><span className="w-app">Rename playlist</span></div>
            <div className="w-dialog-body">
              <input type="text" autoFocus value={renameVal} style={{ width: '100%' }}
                onChange={e => setRenameVal(e.target.value)}
                onKeyDown={e => { if (e.key === 'Enter') doRename(); if (e.key === 'Escape') setRenaming(null) }} />
              <div className="w-toolbar" style={{ marginTop: 8 }}>
                <span style={{ flex: 1 }} />
                <button className="w-btn" onClick={doRename}>OK</button>
                <button className="w-btn" onClick={() => setRenaming(null)}>Cancel</button>
              </div>
            </div>
          </div>
        </div>
      )}

      {confirmDel && (
        <div className="w-overlay" onMouseDown={() => setConfirmDel(null)}>
          <div className="w-dialog w-raised" onMouseDown={e => e.stopPropagation()} style={{ width: 320 }}>
            <div className="w-titlebar"><span className="w-app">Delete playlist</span></div>
            <div className="w-dialog-body">
              <div style={{ marginBottom: 8 }}>
                Delete <b>{confirmDel.name}</b> ({confirmDel.trackCount} tracks)? The tracks stay in the library.
              </div>
              <div className="w-toolbar">
                <span style={{ flex: 1 }} />
                <button className="w-btn" onClick={() => doDelete(confirmDel)}>Delete</button>
                <button className="w-btn" onClick={() => setConfirmDel(null)}>Cancel</button>
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
