import { useEffect, useState } from 'react'
import * as api from './api'

/**
 * Google Cast speakers (Google Home / Nest / Chromecast Audio).
 *
 * The speaker fetches the /stream URL itself, so the important field here is
 * the stream URL: it must resolve on the speakers' own network. Everything is
 * opt-in — nothing is ever cast to a speaker that isn't ticked On.
 */
export default function SpeakersDialog({ onClose }: { onClose: () => void }) {
  const [st, setSt] = useState<api.CastStatusDto | null>(null)
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState('')
  const [err, setErr] = useState('')
  const [urlDraft, setUrlDraft] = useState('')

  const load = () =>
    api.getCastStatus()
      .then(s => { setSt(s); setUrlDraft(s.streamUrlOverride) })
      .catch(e => setErr(e instanceof api.ApiError ? e.message : 'Could not read cast settings.'))

  useEffect(() => { void load() }, [])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  // Every action funnels through here so the buttons can't be double-fired and
  // the result message always lands in the same place.
  const act = async (fn: () => Promise<unknown>, ok?: string) => {
    setBusy(true); setErr(''); setMsg('')
    try {
      await fn()
      await load()
      if (ok) setMsg(ok)
    } catch (e) {
      setErr(e instanceof api.ApiError ? e.message : 'That did not work.')
    } finally {
      setBusy(false)
    }
  }

  const devices = st?.devices ?? []

  return (
    <div className="w-overlay" onMouseDown={onClose}>
      <div className="w-dialog w-raised" onMouseDown={e => e.stopPropagation()} style={{ width: 760, maxWidth: '94vw' }}>
        <div className="w-titlebar">
          <span className="w-app">Speakers (Google Cast)</span>
          <span style={{ flex: 1 }} />
          <button className="w-btn" onClick={onClose} style={{ minHeight: 16, padding: '0 7px' }}>✕</button>
        </div>

        <div className="w-dialog-body">
          <p className="w-muted" style={{ marginTop: 0 }}>
            Plays the live broadcast on Google Home / Nest / Chromecast speakers. The speaker
            fetches the stream itself, so it must be able to reach this server on the network.
            Expect the speakers to run a few seconds behind the main output.
          </p>

          <label className="w-check">
            <input type="checkbox" checked={st?.enabled ?? false} disabled={!st || busy}
              onChange={e => act(() => api.setCastConfig({ enabled: e.target.checked }),
                e.target.checked ? 'Casting switched on.' : 'Casting switched off — all speakers stopped.')} />
            {' '}Enable casting to speakers
          </label>

          <label className="w-check">
            <input type="checkbox" checked={st?.showOnListener ?? false} disabled={!st || !st.enabled || busy}
              onChange={e => act(() => api.setCastConfig({ showOnListener: e.target.checked }))} />
            {' '}Let website visitors start speakers themselves
          </label>
          <div className="w-muted" style={{ margin: '2px 0 6px 22px' }}>
            Visitors only ever see the speakers marked <b>Guests</b> in the list below.
          </div>

          <div className="w-formrow">
            <label>Start volume:</label>
            <input type="number" min={0} max={100} step={5}
              value={Math.round((st?.volume ?? 0) * 100)} disabled={!st || busy}
              onChange={e => act(() => api.setCastConfig({ volume: Math.max(0, Math.min(100, Number(e.target.value))) / 100 }))}
              style={{ width: 64 }} /> %
            <span className="w-muted">set on the speaker when a cast starts (0 = leave it alone)</span>
          </div>

          <fieldset className="w-group">
            <legend>Stream address</legend>
            <div className="w-formrow">
              <label>URL:</label>
              <input type="text" value={urlDraft} placeholder={st?.streamUrl ?? ''} disabled={busy}
                onChange={e => setUrlDraft(e.target.value)} style={{ flex: 1, minWidth: 260 }} />
              <button className="w-btn" disabled={busy}
                onClick={() => act(() => api.setCastConfig({ streamUrl: urlDraft.trim() }), 'Stream address saved.')}>Save</button>
            </div>
            <div className="w-muted">
              Leave empty to use the detected address. Speakers will be sent: <b>{st?.streamUrl ?? '—'}</b>
              {st?.detectedIp ? <> (this server looks like <b>{st.detectedIp}</b>)</> : null}
            </div>
          </fieldset>

          <div style={{ display: 'flex', gap: 6, alignItems: 'center', margin: '8px 0' }}>
            <button className="w-btn" disabled={!st?.enabled || busy}
              onClick={() => act(() => api.discoverCastDevices(), 'Scan finished.')}>Search for speakers</button>
            <button className="w-btn" disabled={busy}
              onClick={() => act(() => api.stopAllCasts(), 'All speakers stopped.')}>Stop all</button>
            {busy && <span className="w-muted">working…</span>}
          </div>

          <table className="w-table w-grid">
            <thead>
              <tr>
                <th>Speaker</th>
                <th style={{ width: 130 }}>Model</th>
                <th style={{ width: 90 }}>Status</th>
                <th style={{ width: 96 }} title="May website visitors start this speaker?">Guests</th>
                <th style={{ width: 190 }} />
              </tr>
            </thead>
            <tbody>
              {devices.map(d => (
                <tr key={d.id} style={d.allowed ? undefined : { opacity: .6 }}>
                  <td title={`${d.host}:${d.port}`}>
                    {d.name || d.id}
                    {d.casting && <span className="w-srcbadge" style={{ marginLeft: 6 }}>playing</span>}
                  </td>
                  <td className="w-muted">{d.model || '---'}</td>
                  <td className="w-muted">{d.online ? 'found' : 'not seen'}</td>
                  <td>
                    <button className="w-btn" disabled={busy || !d.allowed}
                      title={d.allowed
                        ? (d.guestAllowed
                          ? 'Website visitors can start this speaker — click to restrict it to the DJ'
                          : 'Only the DJ can start this speaker — click to let visitors start it')
                        : 'Allow the speaker first'}
                      onClick={() => act(() => api.setCastGuestAllowed(d.id, !d.guestAllowed))}>
                      {d.allowed && d.guestAllowed ? 'Guests' : 'DJ only'}
                    </button>
                  </td>
                  <td style={{ whiteSpace: 'nowrap' }}>
                    <button className="w-btn" disabled={busy}
                      title={d.allowed ? 'Stop using this speaker' : 'Allow this speaker to be used'}
                      onClick={() => act(() => api.setCastAllowed(d.id, !d.allowed))}>
                      {d.allowed ? 'On' : 'Off'}
                    </button>{' '}
                    <button className="w-btn" disabled={busy || !d.allowed || !st?.enabled}
                      title="Play the live broadcast here"
                      onClick={() => act(() => api.playCast(d.id))}>▶ Play</button>{' '}
                    <button className="w-btn" disabled={busy || !d.casting}
                      title="Stop this speaker"
                      onClick={() => act(() => api.stopCast(d.id))}>■ Stop</button>
                  </td>
                </tr>
              ))}
              {devices.length === 0 && (
                <tr><td colSpan={5} className="w-muted" style={{ padding: 8 }}>
                  No speakers yet. Switch casting on, then press “Search for speakers”.
                </td></tr>
              )}
            </tbody>
          </table>

          {msg && <div className="w-muted" style={{ marginTop: 4 }}>{msg}</div>}
          {err && <div className="w-err" style={{ marginTop: 4 }}>{err}</div>}
          <div className="w-muted" style={{ marginTop: 6 }}>
            Nothing found? The speakers must sit on the same network as this server, and Windows
            Firewall has to let the service send mDNS (UDP 5353).
          </div>
        </div>
      </div>
    </div>
  )
}
