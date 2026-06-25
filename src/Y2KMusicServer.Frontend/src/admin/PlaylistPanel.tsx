import { useEffect, useState, type MouseEvent as ReactMouseEvent } from 'react'
import * as api from './api'
import { fmtTime } from './api'
import RequestsPanel from './RequestsPanel'

// Right-click menu geometry, used only to keep it inside the viewport.
const MENU_W = 200
const MENU_H = 84

type RowMenu = { x: number; y: number; entry: api.PlaylistItem }

export default function PlaylistPanel(
  { onPlayNow }: { onPlayNow: (trackId: number) => Promise<unknown> | void }
) {
  const [list, setList] = useState<api.PlaylistItem[]>([])
  const [busy, setBusy] = useState(false)
  const [selId, setSelId] = useState<number | null>(null)
  const [menu, setMenu] = useState<RowMenu | null>(null)

  const refreshList = () => api.getPlaylist().then(setList).catch(() => {})
  useEffect(() => {
    refreshList()
    const id = setInterval(refreshList, 2000) // surface Auto DJ top-ups
    return () => clearInterval(id)
  }, [])

  // Dismiss the row menu on any click, scroll, resize, or Escape. The menu
  // item's own onClick bubbles to the React root before this window listener,
  // so the action still fires.
  useEffect(() => {
    if (!menu) return
    const close = () => setMenu(null)
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setMenu(null) }
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
  }, [menu])

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
    const y = Math.max(4, Math.min(ev.clientY, window.innerHeight - MENU_H - 4))
    setMenu({ x, y, entry: e })
  }

  return (
    <div className="w-panel w-raised w-playlistpanel">
      <div className="w-panelhead">Playlist</div>
      <RequestsPanel onAccepted={refreshList} />
      {/* Click a row to select; double-click plays it now (crossfade);
          right-click for the action menu. */}
      <div className="w-listwrap w-sunken" style={{ flex: 1, minHeight: 0 }}>
        <table className="w-table">
          <thead>
            <tr>
              <th>#</th><th>Title</th><th>Artist</th>
              <th className="w-num">Dur</th><th className="w-num">Mix-in</th>
              <th className="w-num">BPM</th><th className="w-num">LUFS</th>
              <th>Added by</th><th></th>
            </tr>
          </thead>
          <tbody>
            {list.map(e => (
              <tr key={e.id} className={selId === e.id ? 'w-rowsel' : ''}
                onClick={() => setSelId(e.id)}
                onDoubleClick={() => playNowEntry(e)}
                onContextMenu={ev => openMenu(ev, e)}
                title="Double-click to play now (crossfade) · right-click for more">
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
            ))}
            {list.length === 0 && (
              <tr><td colSpan={9} className="w-muted" style={{ padding: 8 }}>Playlist empty. Add tracks, or enable Auto DJ in Settings.</td></tr>
            )}
          </tbody>
        </table>
      </div>
      <div className="w-toolbar">
        <button className="w-btn" disabled={busy || list.length === 0}
          onClick={() => guard(async () => { await api.clearPlaylist(); await refreshList() })}>Clear</button>
        <button className="w-btn" disabled title="Save/Load list — not ported">Save List</button>
        <button className="w-btn" disabled title="Save/Load list — not ported">Load List</button>
      </div>

      {menu && (
        <ul className="w-ctxmenu" role="menu" style={{ left: menu.x, top: menu.y }}
          onContextMenu={e => e.preventDefault()}>
          <li className="w-ctxitem" role="menuitem"
            onClick={() => { playNowEntry(menu.entry); setMenu(null) }}>Play now (crossfade)</li>
          <li className="w-ctxitem" role="menuitem"
            onClick={() => { remove(menu.entry.id); setMenu(null) }}>Remove from playlist</li>
        </ul>
      )}
    </div>
  )
}
