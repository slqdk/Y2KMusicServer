import { useEffect, useState } from 'react'
import * as api from './api'
import { fmtTime } from './api'
import RequestsPanel from './RequestsPanel'

export default function PlaylistPanel() {
  const [list, setList] = useState<api.PlaylistItem[]>([])
  const [busy, setBusy] = useState(false)

  const refreshList = () => api.getPlaylist().then(setList).catch(() => {})
  useEffect(() => {
    refreshList()
    const id = setInterval(refreshList, 2000) // surface Auto DJ top-ups
    return () => clearInterval(id)
  }, [])

  const guard = async (fn: () => Promise<unknown>) => {
    setBusy(true)
    try { await fn() } catch { /* ignore */ } finally { setBusy(false) }
  }

  return (
    <div className="w-panel w-raised w-playlistpanel">
      <div className="w-panelhead">Playlist</div>
      <RequestsPanel onAccepted={refreshList} />
      <div className="w-listwrap w-sunken" style={{ flex: 1, minHeight: 0 }}>
        <table className="w-table">
          <thead>
            <tr><th>#</th><th>Title</th><th>Artist</th><th className="w-num">Dur</th><th>Added by</th><th></th></tr>
          </thead>
          <tbody>
            {list.map(e => (
              <tr key={e.id}>
                <td className="w-num">{e.position + 1}</td>
                <td title={e.title ?? ''}>{e.title ?? '(untitled)'}</td>
                <td title={e.artist ?? ''}>{e.artist ?? '---'}</td>
                <td className="w-num">{fmtTime(e.durationSec)}</td>
                <td><span className="w-srcbadge">{e.addedBy ?? e.source}</span></td>
                <td className="w-rowbtns">
                  <button className="w-btn" disabled={busy} title="Remove"
                    onClick={() => guard(async () => { await api.removeEntry(e.id); await refreshList() })}>✕</button>
                </td>
              </tr>
            ))}
            {list.length === 0 && (
              <tr><td colSpan={6} className="w-muted" style={{ padding: 8 }}>Playlist empty. Add tracks, or enable Auto DJ in Settings.</td></tr>
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
    </div>
  )
}
