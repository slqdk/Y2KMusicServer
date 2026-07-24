import { useEffect, useRef, useState, type MouseEvent as ReactMouseEvent } from 'react'
import * as api from './api'
import { fmtTime } from './api'
import RequestsPanel from './RequestsPanel'
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
  const view = useColumnWidths('y2k.cols.savedlist', [5, 30, 25, 7, 8, 8, 9, 5])

  const refreshList = () => api.getPlaylist().then(setList).catch(() => {})
  const refreshTiles = () =>
    api.getSavedPlaylists().then(r => { setTiles(r.playlists); setMaxTiles(r.max) }).catch(() => {})
  const refreshView = (pl: api.SavedPlaylistDto) =>
    api.getSavedPlaylistTracks(pl.id).then(r => setViewItems(r.items)).catch(() => setViewItems([]))

  const refreshAutoDj = () => api.getAutoDj().then(s => setAutoDj(s.autoDj)).catch(() => {})
  useEffect(() => {
    refreshList(); refreshTiles(); refreshAutoDj()
    const id = setInterval(() => { refreshList(); refreshTiles() }, 2000) // surfaces Auto DJ top-ups + adds
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
      setNote(r.action === 'crossfaded' ? `"${pl.name}" is live — crossfading.`
        : r.action === 'started' ? `"${pl.name}" is live — playback started.`
        : `"${pl.name}" queued.`)
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
        <button className={`w-btn ${autoDj ? 'w-autodj-on' : ''}`} disabled={busy || autoDj == null}
          title="Auto DJ fills the queue from the saved playlists whose timeslot is active (weighted by priority)"
          onClick={toggleAutoDj}>
          Auto DJ: {autoDj == null ? '…' : autoDj ? 'ON' : 'OFF'}
        </button>
      </div>

      {/* Saved-playlist tiles. Click = view; right-click = rename / delete /
          priority / activate; the last free slot is the New-playlist tile. */}
      <div className="w-pltiles">
        {tiles.map(pl => (
          <div key={pl.id}
            className={`w-pltile w-raised ${viewing?.id === pl.id ? 'w-viewing' : ''}`}
            onClick={() => setViewing(v => v?.id === pl.id ? null : pl)}
            onContextMenu={e => openTileMenu(e, pl)}
            title={`Priority ${pl.priority} · ${pl.slotCount} timeslot(s) · right-click for actions`}>
            <div className="w-cat-name">{pl.name}</div>
            <div className="w-cat-count">{pl.trackCount} tracks</div>
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
            <button className="w-btn" disabled={busy}
              title="Replace the live queue with this playlist (requests stay first) and crossfade into it"
              onClick={() => activate(viewing)}>▶ Activate</button>
            <button className="w-btn" onClick={() => setViewing(null)}>Back to live queue</button>
          </div>
          <div className="w-listwrap w-sunken" style={{ flex: 1, minHeight: 0, overflowX: 'hidden' }}>
            <table className="w-table w-grid">
              {view.colgroup}
              <thead>
                <tr>
                  <th className="w-num">#<ColResizer onMouseDown={view.startResize(0)} /></th>
                  <th>Title<ColResizer onMouseDown={view.startResize(1)} /></th>
                  <th>Artist<ColResizer onMouseDown={view.startResize(2)} /></th>
                  <th>Type<ColResizer onMouseDown={view.startResize(3)} /></th>
                  <th className="w-num">Dur<ColResizer onMouseDown={view.startResize(4)} /></th>
                  <th className="w-num">BPM<ColResizer onMouseDown={view.startResize(5)} /></th>
                  <th className="w-num">LUFS<ColResizer onMouseDown={view.startResize(6)} /></th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                {viewItems.map(t => (
                  <tr key={t.entryId}>
                    <td className="w-num">{t.position + 1}</td>
                    <td title={t.title ?? ''}>{t.title ?? '(untitled)'}</td>
                    <td title={t.artist ?? ''}>{t.artist ?? '---'}</td>
                    <td>{t.type ?? '---'}</td>
                    <td className="w-num">{fmtTime(t.durationSec)}</td>
                    <td className="w-num">{t.bpm != null ? Math.round(t.bpm) : '---'}</td>
                    <td className="w-num">{t.lufs != null ? t.lufs.toFixed(1) : '---'}</td>
                    <td className="w-rowbtns">
                      <button className="w-btn" disabled={busy} title="Remove from this playlist"
                        onClick={() => removeViewTrack(t.entryId)}>✕</button>
                    </td>
                  </tr>
                ))}
                {viewItems.length === 0 && (
                  <tr><td colSpan={8} className="w-muted" style={{ padding: 8 }}>
                    Empty. Right-click tracks in the Library and choose “Add to playlist → {viewing.name}”.
                  </td></tr>
                )}
              </tbody>
            </table>
          </div>
        </>
      ) : (
        <>
          {/* Live queue (now playing + upcoming). */}
          <RequestsPanel onAccepted={refreshList} />
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
                  const nowIdx = nowPlayingTrackId == null
                    ? -1 : list.findIndex(x => x.trackId === nowPlayingTrackId)
                  return list.map((e, i) => (
                  <tr key={e.id}
                    ref={i === nowIdx ? nowRowRef : undefined}
                    className={[
                      selId === e.id ? 'w-rowsel' : '',
                      i === nowIdx ? 'w-rownow' : nowIdx >= 0 && i < nowIdx ? 'w-rowplayed' : ''
                    ].filter(Boolean).join(' ')}
                    onClick={() => setSelId(e.id)}
                    onDoubleClick={() => playNowEntry(e)}
                    onContextMenu={ev => openMenu(ev, e)}
                    title={i === nowIdx ? 'Now playing'
                      : nowIdx >= 0 && i < nowIdx ? 'Already played'
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
                {list.length === 0 && (
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
          <li className="w-ctxitem" role="menuitem"
            onClick={() => { activate(tileMenu.pl); setTileMenu(null) }}>▶ Activate</li>
          <li className="w-ctxitem" role="menuitem"
            onClick={() => { setSchedFor(tileMenu.pl); setTileMenu(null) }}>Schedule…</li>
          <li className="w-ctxitem" role="menuitem"
            onClick={() => { setRenaming(tileMenu.pl); setRenameVal(tileMenu.pl.name); setTileMenu(null) }}>Rename…</li>
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
