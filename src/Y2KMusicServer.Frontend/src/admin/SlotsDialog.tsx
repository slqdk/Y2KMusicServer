import { useEffect, useState } from 'react'
import * as api from './api'

const DAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'] // bit 0..6
const SLOTS = 5

const blankSlot = (i: number): api.PlaylistSlot => ({
  slotIndex: i, enabled: false, timeFrom: '08:00', timeTo: '12:00', daysMask: 0
})

function toMin(hhmm: string | null): number | null {
  if (!hhmm) return null
  const m = /^(\d{1,2}):(\d{2})$/.exec(hhmm.trim())
  if (!m) return null
  const h = +m[1], mm = +m[2]
  if (h > 23 || mm > 59) return null
  return h * 60 + mm
}

// Render a slot as 1–2 segments on a 24h track (handles overnight wrap).
function segments(from: number, to: number): Array<{ left: number; width: number }> {
  const span = (a: number, b: number) => ({ left: (a / 1440) * 100, width: ((b - a) / 1440) * 100 })
  return from <= to ? [span(from, to)] : [span(from, 1440), span(0, to)]
}

/**
 * The schedule editor for one saved playlist: up to five day/time slots that
 * decide when Auto DJ may feed from it (priority lives on the playlist tile,
 * not per slot). Same five-row editor + 24-hour visualisation as the retired
 * per-category dialog. Saved as a whole (replace-all PUT).
 */
export default function SlotsDialog({ playlist, onClose, onChanged }:
  { playlist: api.SavedPlaylistDto; onClose: () => void; onChanged: () => void }) {

  const [slots, setSlots] = useState<api.PlaylistSlot[]>(Array.from({ length: SLOTS }, (_, i) => blankSlot(i)))
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [err, setErr] = useState<string | null>(null)

  useEffect(() => {
    api.getSavedPlaylistSlots(playlist.id).then(r => {
      const arr = Array.from({ length: SLOTS }, (_, i) => blankSlot(i))
      r.slots.forEach(s => {
        if (s.slotIndex >= 0 && s.slotIndex < SLOTS)
          arr[s.slotIndex] = {
            slotIndex: s.slotIndex,
            enabled: s.enabled,
            timeFrom: s.timeFrom ?? '08:00',
            timeTo: s.timeTo ?? '12:00',
            daysMask: s.daysMask
          }
      })
      setSlots(arr)
    }).catch(() => setErr('Could not load the schedule.'))
  }, [playlist.id])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  const patchSlot = (i: number, p: Partial<api.PlaylistSlot>) =>
    setSlots(prev => prev.map((s, j) => (j === i ? { ...s, ...p } : s)))

  const toggleDay = (i: number, bit: number) =>
    setSlots(prev => prev.map((s, j) => j === i ? { ...s, daysMask: s.daysMask ^ (1 << bit) } : s))

  const save = async () => {
    setBusy(true); setErr(null); setSaved(false)
    try {
      // Only rows that differ from an untouched blank need storing; sending all
      // five is simpler and the server treats it as replace-all anyway.
      await api.putSavedPlaylistSlots(playlist.id, slots)
      setSaved(true)
      onChanged()
    } catch { setErr('Save failed.') }
    finally { setBusy(false) }
  }

  return (
    <div className="w-overlay" onMouseDown={onClose}>
      <div className="w-dialog w-raised" onMouseDown={e => e.stopPropagation()}
        style={{ width: 560, maxWidth: '94vw' }}>
        <div className="w-titlebar">
          <span className="w-app">Schedule — {playlist.name}</span>
          <span style={{ flex: 1 }} />
          <button className="w-btn" onClick={onClose} style={{ minHeight: 16, padding: '0 7px' }}>✕</button>
        </div>

        <div className="w-dialog-body">
          <div className="w-muted" style={{ marginBottom: 6 }}>
            Auto DJ feeds from this playlist while any enabled slot covers the current day and time.
            No days ticked = every day; a window may wrap past midnight. Priority is set on the tile (right-click).
          </div>

          {slots.map((s, i) => {
            const from = toMin(s.timeFrom)
            const to = toMin(s.timeTo)
            return (
              <fieldset key={i} className="w-group" style={{ marginBottom: 4, opacity: s.enabled ? 1 : .75 }}>
                <legend>
                  <label className="w-check">
                    <input type="checkbox" checked={s.enabled}
                      onChange={e => patchSlot(i, { enabled: e.target.checked })} /> Slot {i + 1}
                  </label>
                </legend>
                <div className="w-toolbar" style={{ flexWrap: 'wrap' }}>
                  <label>From:</label>
                  <input type="time" value={s.timeFrom ?? ''} disabled={!s.enabled}
                    onChange={e => patchSlot(i, { timeFrom: e.target.value || null })} />
                  <label>To:</label>
                  <input type="time" value={s.timeTo ?? ''} disabled={!s.enabled}
                    onChange={e => patchSlot(i, { timeTo: e.target.value || null })} />
                  <span style={{ width: 8 }} />
                  {DAYS.map((d, bit) => (
                    <label key={d} className="w-check" style={{ gap: 2 }}>
                      <input type="checkbox" disabled={!s.enabled}
                        checked={(s.daysMask & (1 << bit)) !== 0}
                        onChange={() => toggleDay(i, bit)} /> {d}
                    </label>
                  ))}
                </div>
                {s.enabled && from != null && to != null && (
                  <div className="w-sunken" style={{ position: 'relative', height: 10, marginTop: 3 }}>
                    {segments(from, to).map((seg, k) => (
                      <div key={k} style={{
                        position: 'absolute', top: 1, bottom: 1,
                        left: `${seg.left}%`, width: `${seg.width}%`,
                        background: 'var(--w-green, #C6E8C6)', border: '1px solid #7AA97A'
                      }} />
                    ))}
                  </div>
                )}
              </fieldset>
            )
          })}

          {err && <div className="w-err" style={{ marginTop: 4 }}>{err}</div>}

          <div className="w-toolbar" style={{ marginTop: 6 }}>
            <span className="w-muted">{saved ? 'Saved.' : ''}</span>
            <span style={{ flex: 1 }} />
            <button className="w-btn" disabled={busy} onClick={save}>Save</button>
            <button className="w-btn" onClick={onClose}>Close</button>
          </div>
        </div>
      </div>
    </div>
  )
}
