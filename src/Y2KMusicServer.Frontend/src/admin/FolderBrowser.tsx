import { useEffect, useState } from 'react'
import * as api from './api'

// Server-side folder browser. The service is headless, so the operator's
// browser can't see the server disk; this navigates drives/directories on the
// host via /api/admin/fs and hands the chosen path back. It renders inside the
// themed admin root, so it inherits whichever Windows skin is active.
export default function FolderBrowser({ onSelect, onClose }:
  { onSelect: (path: string) => void; onClose: () => void }) {

  const [listing, setListing] = useState<api.FsListing | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [err, setErr] = useState<string | null>(null)

  const load = (path: string | null) => {
    setLoading(true); setErr(null)
    api.browseFs(path)
      .then(l => { setListing(l); setSelected(null) })
      .catch(e => setErr(e instanceof api.ApiError
        ? (e.status === 403 ? 'Access to that folder is denied.'
          : e.status === 404 ? 'That folder no longer exists.'
          : 'Could not read that folder.')
        : 'Could not reach the server.'))
      .finally(() => setLoading(false))
  }

  useEffect(() => { load(null) }, []) // start at the drive list

  const canUp = listing != null && !listing.isDriveList
  const target = selected ?? listing?.path ?? null   // what "Select folder" commits
  const canSelect = !!target

  const up = () => { if (canUp) load(listing!.parent && listing!.parent !== '' ? listing!.parent : null) }
  const pick = () => { if (target) { onSelect(target); onClose() } }

  return (
    <div className="w-overlay">
      <div className="w-dialog" style={{ width: 520 }}>
        <div className="w-titlebar"><span className="w-app">Browse for folder</span></div>
        <div className="w-dialog-body">
          <div className="w-toolbar" style={{ marginTop: 0 }}>
            <button className="w-btn" disabled={!canUp || loading} onClick={up}>↑ Up</button>
            <span className="w-muted" style={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {listing?.isDriveList ? 'This PC' : (listing?.path ?? '')}
            </span>
          </div>

          <div className="w-listwrap w-sunken" style={{ height: 300, marginTop: 6 }}>
            <table className="w-table">
              <tbody>
                {(listing?.entries ?? []).map(e => (
                  <tr key={e.path}
                    className={selected === e.path ? 'w-rowsel' : ''}
                    onClick={() => setSelected(e.path)}
                    onDoubleClick={() => load(e.path)}
                    title={e.path}>
                    <td>{e.name}</td>
                  </tr>
                ))}
                {loading && <tr><td className="w-muted" style={{ padding: 8 }}>Loading…</td></tr>}
                {!loading && listing && listing.entries.length === 0 && (
                  <tr><td className="w-muted" style={{ padding: 8 }}>
                    {listing.isDriveList ? 'No drives found.' : 'No sub-folders here — you can still select this folder.'}
                  </td></tr>
                )}
              </tbody>
            </table>
          </div>

          {err && <div className="w-err" style={{ marginTop: 4 }}>{err}</div>}

          <div className="w-toolbar">
            <span className="w-muted" style={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {canSelect ? `Select: ${target}` : 'Double-click a folder to open it'}
            </span>
            <button className="w-btn" onClick={onClose}>Cancel</button>
            <button className="w-btn w-primary" disabled={!canSelect || loading} onClick={pick}>Select folder</button>
          </div>
        </div>
      </div>
    </div>
  )
}
