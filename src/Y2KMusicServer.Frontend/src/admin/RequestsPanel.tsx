import { useEffect, useState } from 'react'
import * as api from './api'

export default function RequestsPanel({ onAccepted }: { onAccepted: () => void }) {
  const [reqs, setReqs] = useState<api.RequestDto[]>([])
  const [busy, setBusy] = useState(false)

  const refresh = () => api.getRequests().then(setReqs).catch(() => {})
  useEffect(() => {
    refresh()
    const id = setInterval(refresh, 3000)
    return () => clearInterval(id)
  }, [])

  const act = async (fn: () => Promise<unknown>, accepted: boolean) => {
    setBusy(true)
    try { await fn(); await refresh(); if (accepted) onAccepted() }
    catch { /* ignore */ } finally { setBusy(false) }
  }

  const pending = reqs.filter(r => r.status === 'Pending')
  const ordered = [...reqs].sort((a, b) =>
    (a.status === 'Pending' ? 0 : 1) - (b.status === 'Pending' ? 0 : 1))

  return (
    <fieldset className="w-group">
      <legend>Requests {pending.length > 0 && `(${pending.length} pending)`}</legend>
      <div className="w-listwrap w-sunken" style={{ maxHeight: 140 }}>
        <table className="w-table">
          <tbody>
            {ordered.map(r => (
              <tr key={r.id} className={r.status === 'Pending' ? '' : 'w-muted'}>
                <td title={r.title ?? ''}>{r.title ?? '(untitled)'}</td>
                <td title={r.artist ?? ''}>{r.artist ?? '---'}</td>
                <td>{r.requesterName ?? '—'}</td>
                <td className="w-rowbtns" style={{ width: 1, whiteSpace: 'nowrap' }}>
                  {r.status === 'Pending' ? (
                    <>
                      <button className="w-btn" disabled={busy} title="Accept → add to playlist"
                        onClick={() => act(() => api.acceptRequest(r.id), true)}>✓</button>
                      <button className="w-btn" disabled={busy} title="Dismiss"
                        onClick={() => act(() => api.dismissRequest(r.id), false)}>✕</button>
                    </>
                  ) : <span className="w-srcbadge">{r.status}</span>}
                </td>
              </tr>
            ))}
            {reqs.length === 0 && (
              <tr><td className="w-muted" style={{ padding: 6 }}>No requests yet.</td></tr>
            )}
          </tbody>
        </table>
      </div>
    </fieldset>
  )
}
