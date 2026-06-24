import { useEffect, useState } from 'react'
import * as api from './api'

// Server-side folder browser. The service is headless, so the operator's
// browser can't see the server disk; this navigates drives/directories on the
// host via /api/admin/fs and hands the chosen path back. It renders inside the
// themed admin root, so it inherits whichever Windows skin is active.
//
// Network folders: the service (LocalSystem) can't read a credentialed SMB
// share on its own, so this dialog can store a username/password per server
// (/api/admin/network/connect). Once connected, a UNC path browses like any
// local folder. Local drives are unaffected — type a path or click a drive.
export default function FolderBrowser({ onSelect, onClose }:
  { onSelect: (path: string) => void; onClose: () => void }) {

  const [listing, setListing] = useState<api.FsListing | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [err, setErr] = useState<string | null>(null)
  const [address, setAddress] = useState('')
  const [shares, setShares] = useState<api.NetworkShare[]>([])

  // Credential view (shown instead of the browser while connecting a share).
  const [showConnect, setShowConnect] = useState(false)
  const [cPath, setCPath] = useState('')
  const [cUser, setCUser] = useState('')
  const [cPass, setCPass] = useState('')
  const [connecting, setConnecting] = useState(false)
  const [connectErr, setConnectErr] = useState<string | null>(null)

  const uncPrefix = (host: string) => '\\\\' + host + '\\'      // -> \\host\
  const isUnc = (p: string) => p.trim().startsWith('\\\\')

  const loadShares = () => { api.getNetworkShares().then(setShares).catch(() => { /* ignore */ }) }

  const load = (path: string | null) => {
    setLoading(true); setErr(null)
    api.browseFs(path)
      .then(l => { setListing(l); setSelected(null); setAddress(l.path ?? '') })
      .catch(e => {
        // A UNC path we aren't authenticated to yet -> offer to connect rather
        // than just reporting "access denied".
        if (e instanceof api.ApiError && e.status === 403 && path && isUnc(path)) {
          openConnect(path)
        } else {
          setErr(e instanceof api.ApiError
            ? (e.status === 403 ? 'Access to that folder is denied.'
              : e.status === 404 ? 'That folder no longer exists.'
              : 'Could not read that folder.')
            : 'Could not reach the server.')
        }
      })
      .finally(() => setLoading(false))
  }

  useEffect(() => { load(null); loadShares() }, []) // start at the drive list

  const openConnect = (prefillPath: string) => {
    setCPath(prefillPath); setCUser(''); setCPass(''); setConnectErr(null); setShowConnect(true)
  }

  const doConnect = () => {
    const path = cPath.trim()
    if (!path) return
    setConnecting(true); setConnectErr(null)
    api.connectNetworkShare({ path, username: cUser, password: cPass })
      .then(r => {
        if (r.ok) { setShowConnect(false); loadShares(); load(path) }
        else setConnectErr(r.error || r.message || 'Could not connect to that share.')
      })
      .catch(() => setConnectErr('Could not reach the server.'))
      .finally(() => setConnecting(false))
  }

  const canUp = listing != null && !listing.isDriveList
  const target = selected ?? listing?.path ?? null   // what "Select folder" commits
  const canSelect = !!target

  const up = () => { if (canUp) load(listing!.parent && listing!.parent !== '' ? listing!.parent : null) }
  const goTo = () => load(address.trim() ? address.trim() : null)
  const pick = () => { if (target) { onSelect(target); onClose() } }

  return (
    <div className="w-overlay">
      <div className="w-dialog" style={{ width: 560 }}>
        <div className="w-titlebar"><span className="w-app">Browse for folder</span></div>
        <div className="w-dialog-body">

          {showConnect ? (
            // ── Connect a network folder ───────────────────────────────
            <div>
              <div className="w-muted" style={{ marginBottom: 6 }}>
                Connect to a network folder. The service stores these credentials
                (encrypted) and uses them to read the share.
              </div>
              <fieldset className="w-group">
                <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
                  <label>Folder path
                    <input type="text" value={cPath} autoFocus
                      placeholder={'\\\\server\\share'}
                      onChange={e => setCPath(e.target.value)}
                      style={{ width: '100%' }} />
                  </label>
                  <label>Username
                    <input type="text" value={cUser}
                      placeholder={'user, MACHINE\\user, or DOMAIN\\user'}
                      onChange={e => setCUser(e.target.value)}
                      style={{ width: '100%' }} />
                  </label>
                  <label>Password
                    <input type="password" value={cPass}
                      onChange={e => setCPass(e.target.value)}
                      onKeyDown={e => { if (e.key === 'Enter') doConnect() }}
                      style={{ width: '100%' }} />
                  </label>
                </div>
              </fieldset>

              {connectErr && <div className="w-err" style={{ marginTop: 4 }}>{connectErr}</div>}

              <div className="w-toolbar">
                <span className="w-muted" style={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  Reused for every folder on this server.
                </span>
                <button className="w-btn" disabled={connecting} onClick={() => setShowConnect(false)}>Back</button>
                <button className="w-btn w-primary" disabled={connecting || !cPath.trim()} onClick={doConnect}>
                  {connecting ? 'Connecting…' : 'Connect'}
                </button>
              </div>
            </div>
          ) : (
            // ── Browse ─────────────────────────────────────────────────
            <>
              <div className="w-toolbar" style={{ marginTop: 0 }}>
                <input type="text" value={address}
                  placeholder={'\\\\server\\share  or  C:\\Music'}
                  onChange={e => setAddress(e.target.value)}
                  onKeyDown={e => { if (e.key === 'Enter') goTo() }}
                  style={{ flex: 1 }} />
                <button className="w-btn" disabled={loading} onClick={goTo}>Go</button>
                <button className="w-btn" disabled={loading} onClick={() => openConnect('\\\\')}>Network…</button>
              </div>

              {listing?.isDriveList && shares.length > 0 && (
                <div className="w-toolbar" style={{ flexWrap: 'wrap', gap: 4 }}>
                  <span className="w-muted">Network:</span>
                  {shares.map(s => (
                    <button key={s.host} className="w-btn" title={uncPrefix(s.host)}
                      onClick={() => setAddress(uncPrefix(s.host))}>
                      {'\\\\' + s.host}
                    </button>
                  ))}
                </div>
              )}

              <div className="w-toolbar" style={{ marginTop: 6 }}>
                <button className="w-btn" disabled={!canUp || loading} onClick={up}>↑ Up</button>
                <span className="w-muted" style={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {listing?.isDriveList ? 'This PC' : (listing?.path ?? '')}
                </span>
              </div>

              <div className="w-listwrap w-sunken" style={{ height: 280, marginTop: 6 }}>
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
                  {canSelect ? `Select: ${target}` : 'Type a path, or double-click a folder to open it'}
                </span>
                <button className="w-btn" onClick={onClose}>Cancel</button>
                <button className="w-btn w-primary" disabled={!canSelect || loading} onClick={pick}>Select folder</button>
              </div>
            </>
          )}

        </div>
      </div>
    </div>
  )
}
