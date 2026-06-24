import { useEffect, useState } from 'react'
import * as api from './api'
import FolderBrowser from './FolderBrowser'

const DAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'] // bit 0..6
const SLOTS = 5

const blankSlot = (): api.SlotInput => ({
  enabled: false, timeFromHHmm: '08:00', timeToHHmm: '12:00', daysMask: 0, priority: 3
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

export default function CategoryDialog({ category, onClose, onChanged }:
  { category: api.CategoryDto; onClose: () => void; onChanged: () => void }) {

  const [name, setName] = useState(category.name)
  const [renameErr, setRenameErr] = useState<string | null>(null)
  const [folders, setFolders] = useState<api.FolderDto[]>([])
  const [newPath, setNewPath] = useState('')
  const [slots, setSlots] = useState<api.SlotInput[]>(Array.from({ length: SLOTS }, blankSlot))
  const [saved, setSaved] = useState(false)
  const [busy, setBusy] = useState(false)
  const [browsing, setBrowsing] = useState(false)

  const refreshFolders = () => api.getFolders(category.id).then(setFolders).catch(() => {})

  useEffect(() => {
    refreshFolders()
    api.getSlots(category.id).then(loaded => {
      const arr = Array.from({ length: SLOTS }, blankSlot)
      loaded.forEach(s => {
        if (s.slotIndex >= 0 && s.slotIndex < SLOTS)
          arr[s.slotIndex] = {
            enabled: s.enabled,
            timeFromHHmm: s.timeFromHHmm ?? '08:00',
            timeToHHmm: s.timeToHHmm ?? '12:00',
            daysMask: s.daysMask,
            priority: s.priority
          }
      })
      setSlots(arr)
    }).catch(() => {})
  }, [category.id])

  const patchSlot = (i: number, p: Partial<api.SlotInput>) =>
    setSlots(prev => prev.map((s, j) => (j === i ? { ...s, ...p } : s)))

  const toggleDay = (i: number, bit: number) =>
    setSlots(prev => prev.map((s, j) => j === i ? { ...s, daysMask: s.daysMask ^ (1 << bit) } : s))

  const doRename = async () => {
    setRenameErr(null); setBusy(true)
    try { const r = await api.renameCategory(category.id, name); setName(r.name); onChanged() }
    catch (e) { setRenameErr(e instanceof api.ApiError ? e.message : 'Rename failed') }
    finally { setBusy(false) }
  }

  const addFolder = async () => {
    const p = newPath.trim()
    if (!p) return
    setBusy(true)
    try { await api.addFolder(category.id, p); setNewPath(''); await refreshFolders(); onChanged() }
    catch { /* ignore */ } finally { setBusy(false) }
  }

  const removeFolder = async (fid: number) => {
    setBusy(true)
    try { await api.removeFolder(category.id, fid); await refreshFolders(); onChanged() }
    catch { /* ignore */ } finally { setBusy(false) }
  }

  const saveSlots = async () => {
    setBusy(true); setSaved(false)
    try { const r = await api.putSlots(category.id, slots); 
      const arr = Array.from({ length: SLOTS }, blankSlot)
      r.forEach(s => { if (s.slotIndex < SLOTS) arr[s.slotIndex] = { enabled: s.enabled, timeFromHHmm: s.timeFromHHmm ?? '08:00', timeToHHmm: s.timeToHHmm ?? '12:00', daysMask: s.daysMask, priority: s.priority } })
      setSlots(arr); setSaved(true); onChanged()
    } catch { /* ignore */ } finally { setBusy(false) }
  }

  return (
    <>
    <div className="w-overlay" onMouseDown={onClose}>
      <div className="w-dialog w-raised" onMouseDown={e => e.stopPropagation()}>
        <div className="w-titlebar">
          <span className="w-app">{category.name} — settings</span>
          <span style={{ flex: 1 }} />
          <button className="w-btn" onClick={onClose} style={{ minHeight: 16, padding: '0 7px' }}>✕</button>
        </div>

        <div className="w-dialog-body">
          {/* Rename */}
          <fieldset className="w-group">
            <legend>Name</legend>
            {category.isCustom ? (
              <div className="w-toolbar">
                <input type="text" value={name} maxLength={40} onChange={e => setName(e.target.value)} style={{ flex: 1 }} />
                <button className="w-btn" disabled={busy || !name.trim()} onClick={doRename}>Rename</button>
              </div>
            ) : (
              <span className="w-muted">{category.name} is a built-in category and can't be renamed.</span>
            )}
            {renameErr && <div className="w-err">{renameErr}</div>}
          </fieldset>

          {/* Folders */}
          <fieldset className="w-group">
            <legend>Folders</legend>
            <div className="w-listwrap w-sunken" style={{ maxHeight: 120 }}>
              <table className="w-table">
                <tbody>
                  {folders.map(f => (
                    <tr key={f.id}>
                      <td title={f.path}>{f.path}</td>
                      <td className="w-rowbtns" style={{ width: 1 }}>
                        <button className="w-btn" disabled={busy} onClick={() => removeFolder(f.id)}>✕</button>
                      </td>
                    </tr>
                  ))}
                  {folders.length === 0 && <tr><td className="w-muted" style={{ padding: 6 }}>No folders assigned.</td></tr>}
                </tbody>
              </table>
            </div>
            <div className="w-toolbar">
              <label htmlFor="addfolder">Path:</label>
              <input id="addfolder" type="text" value={newPath}
                placeholder="Browse… or type a path — e.g. C:\Music\Dance"
                style={{ flex: 1 }}
                onChange={e => setNewPath(e.target.value)}
                onKeyDown={e => { if (e.key === 'Enter') addFolder() }} />
              <button className="w-btn" disabled={busy} onClick={() => setBrowsing(true)}>Browse…</button>
              <button className="w-btn" disabled={busy || !newPath.trim()} onClick={addFolder}>Add folder</button>
            </div>
            <div className="w-muted">
              Use Browse… to pick a folder on the server, or type a path directly (accepted even if the
              drive isn't mounted yet), then click Add folder. A category needs at least one folder before
              it can be enabled. The first folder you add also seeds a default 01:00–23:00 daily schedule
              (editable below); scanning and analysis then run automatically.
            </div>
          </fieldset>

          {/* Schedule */}
          <fieldset className="w-group">
            <legend>Auto DJ schedule</legend>
            <table className="w-table w-slots">
              <thead>
                <tr>
                  <th>On</th><th>From</th><th>To</th>
                  {DAYS.map(d => <th key={d} style={{ textAlign: 'center' }}>{d[0]}</th>)}
                  <th>Pri</th>
                </tr>
              </thead>
              <tbody>
                {slots.map((s, i) => (
                  <tr key={i}>
                    <td><input type="checkbox" checked={s.enabled} onChange={e => patchSlot(i, { enabled: e.target.checked })} /></td>
                    <td><input type="text" value={s.timeFromHHmm ?? ''} disabled={!s.enabled} style={{ width: 48 }}
                      onChange={e => patchSlot(i, { timeFromHHmm: e.target.value })} /></td>
                    <td><input type="text" value={s.timeToHHmm ?? ''} disabled={!s.enabled} style={{ width: 48 }}
                      onChange={e => patchSlot(i, { timeToHHmm: e.target.value })} /></td>
                    {DAYS.map((d, b) => (
                      <td key={d} style={{ textAlign: 'center' }}>
                        <input type="checkbox" disabled={!s.enabled}
                          checked={(s.daysMask & (1 << b)) !== 0}
                          onChange={() => toggleDay(i, b)} />
                      </td>
                    ))}
                    <td>
                      <select value={s.priority} disabled={!s.enabled} onChange={e => patchSlot(i, { priority: Number(e.target.value) })}>
                        {[1, 2, 3, 4, 5].map(p => <option key={p} value={p}>{p}</option>)}
                      </select>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            <div className="w-muted" style={{ margin: '4px 0' }}>
              Priority 1 = most play, 5 = least. No days ticked = every day. Times wrap past midnight (e.g. 22:00→02:00).
            </div>

            {/* 24h timeline */}
            <div className="w-timeline">
              {[0, 6, 12, 18, 24].map(h => (
                <span key={h} className="w-tl-tick" style={{ left: `${(h / 24) * 100}%` }}>{h}</span>
              ))}
              {slots.map((s, i) => {
                if (!s.enabled) return null
                const from = toMin(s.timeFromHHmm), to = toMin(s.timeToHHmm)
                if (from == null || to == null) return null
                return segments(from, to).map((seg, k) => (
                  <div key={`${i}-${k}`} className="w-tl-seg"
                    style={{ left: `${seg.left}%`, width: `${seg.width}%`, opacity: 1 - (s.priority - 1) * 0.15 }}
                    title={`Slot ${i + 1}: ${s.timeFromHHmm}–${s.timeToHHmm} (pri ${s.priority})`} />
                ))
              })}
            </div>

            <div className="w-toolbar">
              <button className="w-btn w-primary" disabled={busy} onClick={saveSlots}>Save schedule</button>
              {saved && <span className="w-muted">Saved.</span>}
            </div>
          </fieldset>
        </div>
      </div>
    </div>
    {browsing && (
      <FolderBrowser onSelect={setNewPath} onClose={() => setBrowsing(false)} />
    )}
    </>
  )
}
