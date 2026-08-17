import { useEffect, useState } from 'react'
import * as api from './api'
import FolderBrowser from './FolderBrowser'

/**
 * The global scan-folder list — the one place music folders are assigned
 * (replaces the per-category folder dialogs). Adding a folder scans it
 * automatically; per-folder Rescan / Clear data / Remove mirror the old
 * category-folder actions with the same innermost-folder-wins ownership.
 */
export default function FoldersDialog({ onClose, onChanged }:
  { onClose: () => void; onChanged: () => void }) {

  const [folders, setFolders] = useState<api.ScanFolderDto[]>([])
  const [newPath, setNewPath] = useState('')
  const [busy, setBusy] = useState(false)
  const [browsing, setBrowsing] = useState(false)
  const [msg, setMsg] = useState<string | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [confirm, setConfirm] = useState<{ folder: api.ScanFolderDto; kind: 'clear' | 'remove' } | null>(null)

  const refresh = () => api.getScanFolders().then(r => setFolders(r.folders)).catch(() => {})
  useEffect(() => { refresh() }, [])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return
      if (confirm) setConfirm(null)
      else onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose, confirm])

  const act = async (fn: () => Promise<unknown>, done?: string) => {
    setBusy(true); setErr(null)
    try { await fn(); await refresh(); onChanged(); if (done) setMsg(done) }
    catch (e) { setErr(e instanceof api.ApiError ? e.message : 'The action failed.') }
    finally { setBusy(false) }
  }

  const add = () => {
    const p = newPath.trim()
    if (!p) return
    act(async () => { await api.addScanFolder(p); setNewPath('') }, 'Folder added — scanning…')
  }

  const runConfirm = () => {
    if (!confirm) return
    const { folder, kind } = confirm
    setConfirm(null)
    if (kind === 'clear')
      act(() => api.clearScanFolder(folder.id), `Cleared the tracks under ${folder.path}.`)
    else
      act(() => api.removeScanFolder(folder.id, true), `Removed ${folder.path} and its tracks.`)
  }

  return (
    <div className="w-overlay" onMouseDown={onClose}>
      <div className="w-dialog w-raised" onMouseDown={e => e.stopPropagation()}
        style={{ width: 620, maxWidth: '94vw' }}>
        <div className="w-titlebar">
          <span className="w-app">Music folders</span>
          <span style={{ flex: 1 }} />
          <button className="w-btn" onClick={onClose} style={{ minHeight: 16, padding: '0 7px' }}>✕</button>
        </div>

        <div className="w-dialog-body">
          <div className="w-muted" style={{ marginBottom: 6 }}>
            The library is built from these folders (subfolders included). Adding a folder scans it right away;
            new tracks land in the flat library and are filtered by Format / Genre / Decade.
          </div>

          <div className="w-toolbar">
            <input type="text" value={newPath} style={{ flex: 1 }} disabled={busy}
              onChange={e => setNewPath(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter') add() }}
              placeholder="C:\Music or \\server\share\Music" />
            <button className="w-btn" disabled={busy} onClick={() => setBrowsing(true)}>Browse…</button>
            <button className="w-btn" disabled={busy || !newPath.trim()} onClick={add}>Add</button>
          </div>

          <div className="w-listwrap w-sunken" style={{ maxHeight: 260, marginTop: 6 }}>
            <table className="w-table">
              <thead>
                <tr><th>Path</th><th className="w-num">Tracks</th><th style={{ width: 236 }} /></tr>
              </thead>
              <tbody>
                {folders.map(f => (
                  <tr key={f.id} style={f.active ? undefined : { opacity: .55 }}>
                    <td title={f.path} style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>
                      {f.path}{!f.exists && <span className="w-err"> (not reachable)</span>}
                      {!f.active && <span className="w-muted"> (hidden from search)</span>}
                    </td>
                    <td className="w-num">{f.trackCount}</td>
                    <td style={{ whiteSpace: 'nowrap' }}>
                      <button className="w-btn" disabled={busy}
                        title={f.active
                          ? 'Hide this folder’s tracks from search (playlists and playback are not affected)'
                          : 'Show this folder’s tracks in search again'}
                        onClick={() => act(() => api.setScanFolderActive(f.id, !f.active),
                          f.active ? `Hid ${f.path} from search.` : `${f.path} is searchable again.`)}>
                        {f.active ? 'On' : 'Off'}
                      </button>{' '}
                      <button className="w-btn" disabled={busy} title="Scan this folder for new files"
                        onClick={() => act(() => api.rescanScanFolder(f.id), 'Rescan queued.')}>↻</button>{' '}
                      <button className="w-btn" disabled={busy} title="Remove this folder's tracks from the library (keeps the folder assigned)"
                        onClick={() => setConfirm({ folder: f, kind: 'clear' })}>Clear</button>{' '}
                      <button className="w-btn" disabled={busy} title="Remove the folder and its tracks"
                        onClick={() => setConfirm({ folder: f, kind: 'remove' })}>Remove</button>
                    </td>
                  </tr>
                ))}
                {folders.length === 0 && (
                  <tr><td colSpan={3} className="w-muted" style={{ padding: 8 }}>No folders yet — add your music folder above.</td></tr>
                )}
              </tbody>
            </table>
          </div>

          {msg && <div className="w-muted" style={{ marginTop: 4 }}>{msg}</div>}
          {err && <div className="w-err" style={{ marginTop: 4 }}>{err}</div>}
        </div>

        {/* Blocking confirmation for the destructive actions. Clicking the
            backdrop or Escape cancels; only the explicit button proceeds. */}
        {confirm && (
          <div className="w-overlay" onMouseDown={() => setConfirm(null)}>
            <div className="w-dialog w-raised" onMouseDown={e => e.stopPropagation()} style={{ width: 420, maxWidth: '90vw' }}>
              <div className="w-titlebar">
                <span className="w-app">{confirm.kind === 'clear' ? 'Clear folder tracks?' : 'Remove folder?'}</span>
                <span style={{ flex: 1 }} />
                <button className="w-btn" onClick={() => setConfirm(null)} style={{ minHeight: 16, padding: '0 7px' }}>✕</button>
              </div>
              <div className="w-dialog-body">
                <div style={{ marginBottom: 10 }}>
                  {confirm.kind === 'clear'
                    ? <>This removes <b>every track</b> under<br /><b>{confirm.folder.path}</b><br />({confirm.folder.trackCount} track{confirm.folder.trackCount === 1 ? '' : 's'}) from the library. The files stay on disk; the folder stays assigned and can be rescanned.</>
                    : <>This removes the folder<br /><b>{confirm.folder.path}</b><br />AND its {confirm.folder.trackCount} track{confirm.folder.trackCount === 1 ? '' : 's'} from the library. The files stay on disk.</>}
                </div>
                <div className="w-muted" style={{ marginBottom: 10 }}>
                  Tip: if you only want the folder out of search results, use its On/Off button instead — that hides without deleting.
                </div>
                <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end' }}>
                  <button className="w-btn" onClick={() => setConfirm(null)}>Cancel</button>
                  <button className="w-btn" onClick={runConfirm}>
                    {confirm.kind === 'clear' ? 'Yes, clear the tracks' : 'Yes, remove the folder'}
                  </button>
                </div>
              </div>
            </div>
          </div>
        )}

        {browsing && (
          <FolderBrowser
            onSelect={p => { setNewPath(p); setBrowsing(false) }}
            onClose={() => setBrowsing(false)} />
        )}
      </div>
    </div>
  )
}
