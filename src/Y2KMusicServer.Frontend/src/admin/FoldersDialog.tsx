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
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

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
        style={{ width: 560, maxWidth: '94vw' }}>
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
                <tr><th>Path</th><th className="w-num">Tracks</th><th style={{ width: 190 }} /></tr>
              </thead>
              <tbody>
                {folders.map(f => (
                  <tr key={f.id}>
                    <td title={f.path} style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>
                      {f.path}{!f.exists && <span className="w-err"> (not reachable)</span>}
                    </td>
                    <td className="w-num">{f.trackCount}</td>
                    <td style={{ whiteSpace: 'nowrap' }}>
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

          {confirm && (
            <div className="w-group" style={{ marginTop: 8, padding: 8 }}>
              <div style={{ marginBottom: 6 }}>
                {confirm.kind === 'clear'
                  ? <>Remove every track under <b>{confirm.folder.path}</b> from the library? The files stay on disk; the folder stays assigned.</>
                  : <>Remove <b>{confirm.folder.path}</b> and its {confirm.folder.trackCount} track(s) from the library? The files stay on disk.</>}
              </div>
              <button className="w-btn" onClick={runConfirm}>Yes</button>{' '}
              <button className="w-btn" onClick={() => setConfirm(null)}>No</button>
            </div>
          )}
        </div>

        {browsing && (
          <FolderBrowser
            onSelect={p => { setNewPath(p); setBrowsing(false) }}
            onClose={() => setBrowsing(false)} />
        )}
      </div>
    </div>
  )
}
