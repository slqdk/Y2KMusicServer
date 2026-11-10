import { useEffect, useRef, useState } from 'react'
import * as api from './api'

/**
 * File → Move / backup. Moves a whole instance to another machine:
 *
 *   old machine  Export   → one zip: library, playlists, schedules, queue,
 *                           settings, every JSON store
 *   new machine  Import   → upload, check what's inside, confirm; the service
 *                           restarts and swaps the data in at startup
 *                Move…    → (Music folders) point any folder that lives
 *                           somewhere else on this machine at its new place
 *
 * Track ids survive the whole trip, which is what keeps every playlist intact.
 */
export default function BackupDialog({ onClose }: { onClose: () => void }) {

  const [staged, setStaged] = useState<api.StagedImport | null>(null)
  const [uploading, setUploading] = useState(false)
  const [phase, setPhase] = useState<'idle' | 'restarting' | 'manual' | 'done'>('idle')
  const [err, setErr] = useState<string | null>(null)
  const fileRef = useRef<HTMLInputElement>(null)

  // A staged import survives the dialog closing (not a restart) — pick it up.
  useEffect(() => { api.getPendingImport().then(setStaged).catch(() => {}) }, [])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && phase !== 'restarting') onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose, phase])

  const choose = async (file: File | undefined) => {
    if (!file) return
    setUploading(true); setErr(null); setStaged(null)
    try { setStaged(await api.uploadImport(file)) }
    catch (e) { setErr(e instanceof api.ApiError ? e.message : 'The upload failed — no reply from the server.') }
    finally {
      setUploading(false)
      if (fileRef.current) fileRef.current.value = ''   // the same file can be chosen again
    }
  }

  const discard = async () => {
    try { await api.discardImport() } catch { /* nothing staged is fine */ }
    setStaged(null)
  }

  // Confirm, then watch for the server to come back as a NEW process: the
  // start time changing is the only reliable sign the import was applied.
  const apply = async () => {
    setErr(null)
    const before = await api.getServiceStartedAt()
    try {
      const r = await api.applyImport()
      if (!r.restarting) { setPhase('manual'); return }
    } catch (e) {
      setErr(e instanceof api.ApiError ? e.message : 'Could not confirm the import.')
      return
    }
    setPhase('restarting')
    const deadline = Date.now() + 3 * 60_000
    while (Date.now() < deadline) {
      await new Promise(res => setTimeout(res, 2000))
      const now = await api.getServiceStartedAt()
      if (now && now !== before) { setPhase('done'); return }
    }
    setErr('The server has not come back yet. Check the tray, or restart the service from there.')
    setPhase('manual')
  }

  const m = staged?.manifest
  const missing = staged?.folders.filter(f => !f.foundHere) ?? []

  return (
    <div className="w-overlay" onMouseDown={() => phase !== 'restarting' && onClose()}>
      <div className="w-dialog w-raised" onMouseDown={e => e.stopPropagation()}
        style={{ width: 600, maxWidth: '94vw' }}>
        <div className="w-titlebar">
          <span className="w-app">Move / backup</span>
          <span style={{ flex: 1 }} />
          <button className="w-btn" disabled={phase === 'restarting'} onClick={onClose}
            style={{ minHeight: 16, padding: '0 7px' }}>✕</button>
        </div>

        <div className="w-dialog-body">
          <fieldset className="w-group">
            <legend>Export</legend>
            <div className="w-muted" style={{ marginBottom: 6 }}>
              Everything this install knows, in one file: the library with its analysis, saved playlists
              and their schedules, the queue, jingles, speakers, and every setting. Safe while music is
              playing. The music itself is not included — it stays where it is.
            </div>
            <div className="w-muted" style={{ marginBottom: 6 }}>
              Not included: network-share passwords (they can only be read on this machine — enter them
              again on the new one) and caches that rebuild themselves.
            </div>
            <a className="w-btn" href={api.exportInstanceUrl} download
              style={{ display: 'inline-block', textDecoration: 'none' }}>Download export</a>
          </fieldset>

          <fieldset className="w-group" style={{ marginTop: 10 }}>
            <legend>Import</legend>

            {phase === 'idle' && !staged && (
              <>
                <div className="w-muted" style={{ marginBottom: 6 }}>
                  Install the same version on the new machine, then choose the export here. You’ll see what’s
                  inside before anything changes.
                </div>
                <input ref={fileRef} type="file" accept=".zip,application/zip" style={{ display: 'none' }}
                  onChange={e => choose(e.target.files?.[0])} />
                <button className="w-btn" disabled={uploading} onClick={() => fileRef.current?.click()}>
                  {uploading ? 'Uploading and checking…' : 'Choose export file…'}
                </button>
              </>
            )}

            {phase === 'idle' && staged && m && (
              <>
                <div className="w-sunken" style={{ padding: '6px 8px', background: 'var(--w-field, #fff)' }}>
                  <div>
                    From <b>{m.machine}</b>, exported {new Date(m.exportedUtc).toLocaleString()} (version {m.appVersion}).
                  </div>
                  <div>
                    <b>{m.tracks}</b> tracks, <b>{m.playlists}</b> saved playlist{m.playlists === 1 ? '' : 's'}
                    {staged.youTubeFiles > 0 && <>, <b>{staged.youTubeFiles}</b> downloaded YouTube file{staged.youTubeFiles === 1 ? '' : 's'}</>}.
                  </div>
                  {staged.folders.length > 0 && (
                    <table className="w-table" style={{ marginTop: 6 }}>
                      <tbody>
                        {staged.folders.map(f => (
                          <tr key={f.path}>
                            <td style={{ width: 18 }}>{f.foundHere ? '✓' : '✗'}</td>
                            <td title={f.path} style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{f.path}</td>
                            <td className={f.foundHere ? 'w-muted' : 'w-err'} style={{ whiteSpace: 'nowrap' }}>
                              {f.foundHere ? 'found here' : 'not on this machine'}
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  )}
                </div>

                {missing.length > 0 && (
                  <div className="w-muted" style={{ marginTop: 6 }}>
                    That’s fine: after the import, open <b>Music folders</b> and use <b>Move…</b> on each
                    folder marked ✗ to point it at where the music lives on this machine. Playlists keep working.
                  </div>
                )}

                <div className="w-err" style={{ marginTop: 6 }}>
                  Importing replaces this install’s library, playlists, queue and settings. What’s here now is
                  not deleted — it’s kept in a <code>before-import-…</code> folder in the data folder.
                </div>

                <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end', marginTop: 8 }}>
                  <button className="w-btn" onClick={discard}>Discard</button>
                  <button className="w-btn w-primary" onClick={apply}>Import and restart</button>
                </div>
              </>
            )}

            {phase === 'restarting' && (
              <div>
                Importing — the server is restarting. This page reconnects by itself; it usually takes
                10–30 seconds.
              </div>
            )}

            {phase === 'manual' && (
              <div>
                The import is confirmed and applies the next time the server starts. Restart it now
                (tray → Restart service), then reload this page.
              </div>
            )}

            {phase === 'done' && (
              <>
                <div>
                  Imported. Reload the page to see the imported library and playlists
                  {missing.length > 0
                    ? <>, then open <b>Folders…</b> above the library and use <b>Move…</b> on each folder marked not reachable</>
                    : ''}.
                </div>
                <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end', marginTop: 8 }}>
                  <button className="w-btn w-primary" onClick={() => window.location.reload()}>Reload page</button>
                </div>
              </>
            )}
          </fieldset>

          {err && <div className="w-err" style={{ marginTop: 6 }}>{err}</div>}
        </div>
      </div>
    </div>
  )
}
