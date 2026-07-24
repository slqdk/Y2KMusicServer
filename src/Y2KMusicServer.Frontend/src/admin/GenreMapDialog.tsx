import { useEffect, useState } from 'react'
import * as api from './api'

/**
 * The genre-map editor: the buckets the library is filtered by, plus rules
 * mapping raw tag genres onto them. The map applies at query time, so Save
 * re-buckets the whole library instantly — no rescan. The raw-genre worklist
 * (right side) shows every distinct tag genre in the library with its count
 * and where it currently lands; clicking one prefills a rule for it.
 */
export default function GenreMapDialog({ onClose, onChanged }:
  { onClose: () => void; onChanged: () => void }) {

  const [map, setMap] = useState<api.GenreMap | null>(null)
  const [raws, setRaws] = useState<api.RawGenre[]>([])
  const [untagged, setUntagged] = useState(0)
  const [newBucket, setNewBucket] = useState('')
  const [ruleRaw, setRuleRaw] = useState('')
  const [ruleSub, setRuleSub] = useState(false)
  const [ruleBucket, setRuleBucket] = useState('')
  const [busy, setBusy] = useState(false)
  const [dirty, setDirty] = useState(false)
  const [err, setErr] = useState<string | null>(null)

  const refreshRaws = () =>
    api.getRawGenres().then(r => { setRaws(r.items); setUntagged(r.untagged) }).catch(() => {})

  useEffect(() => {
    api.getGenreMap().then(setMap).catch(() => setErr('Could not load the genre map.'))
    refreshRaws()
  }, [])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  const edit = (m: api.GenreMap) => { setMap(m); setDirty(true) }

  const addBucket = () => {
    const b = newBucket.trim()
    if (!map || !b) return
    if (map.buckets.some(x => x.toLowerCase() === b.toLowerCase())) { setNewBucket(''); return }
    edit({ ...map, buckets: [...map.buckets, b] })
    setNewBucket('')
  }

  const removeBucket = (b: string) => {
    if (!map) return
    // Rules pointing at a removed bucket would resolve to Unknown — drop them too.
    edit({
      buckets: map.buckets.filter(x => x !== b),
      rules: map.rules.filter(r => r.bucket.toLowerCase() !== b.toLowerCase())
    })
  }

  const addRule = () => {
    const raw = ruleRaw.trim()
    if (!map || !raw || !ruleBucket) return
    const rest = map.rules.filter(r =>
      !(r.raw.toLowerCase() === raw.toLowerCase() && r.substring === ruleSub))
    edit({ ...map, rules: [...rest, { raw, substring: ruleSub, bucket: ruleBucket }] })
    setRuleRaw(''); setRuleSub(false)
  }

  const removeRule = (i: number) => {
    if (!map) return
    edit({ ...map, rules: map.rules.filter((_, j) => j !== i) })
  }

  const save = async () => {
    if (!map) return
    setBusy(true); setErr(null)
    try {
      const saved = await api.putGenreMap(map)
      setMap(saved); setDirty(false)
      await refreshRaws()
      onChanged()
    } catch { setErr('Save failed.') }
    finally { setBusy(false) }
  }

  return (
    <div className="w-overlay" onMouseDown={onClose}>
      <div className="w-dialog w-raised" onMouseDown={e => e.stopPropagation()}
        style={{ width: 700, maxWidth: '96vw' }}>
        <div className="w-titlebar">
          <span className="w-app">Genre map</span>
          <span style={{ flex: 1 }} />
          <button className="w-btn" onClick={onClose} style={{ minHeight: 16, padding: '0 7px' }}>✕</button>
        </div>

        <div className="w-dialog-body">
          {err && <div className="w-err" style={{ marginBottom: 4 }}>{err}</div>}
          {!map && !err && <div className="w-muted" style={{ padding: 4 }}>Loading…</div>}

          {map && (
            <div style={{ display: 'flex', gap: 10, alignItems: 'stretch' }}>
              {/* Left: buckets + rules */}
              <div style={{ flex: 1, minWidth: 0 }}>
                <fieldset className="w-group">
                  <legend>Genre buckets</legend>
                  <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4, marginBottom: 6 }}>
                    {map.buckets.map(b => (
                      <span key={b} className="w-raised" style={{ padding: '1px 4px', whiteSpace: 'nowrap' }}>
                        {b}{' '}
                        <button className="w-btn" style={{ minHeight: 14, padding: '0 4px' }}
                          title={`Remove "${b}" (its rules go too; tracks fall back to Unknown)`}
                          onClick={() => removeBucket(b)}>✕</button>
                      </span>
                    ))}
                    <span className="w-muted" style={{ alignSelf: 'center' }}>+ Unknown (always)</span>
                  </div>
                  <div className="w-toolbar">
                    <input type="text" value={newBucket} style={{ flex: 1 }}
                      onChange={e => setNewBucket(e.target.value)}
                      onKeyDown={e => { if (e.key === 'Enter') addBucket() }}
                      placeholder="New bucket, e.g. Dansk Musik" />
                    <button className="w-btn" disabled={!newBucket.trim()} onClick={addBucket}>Add</button>
                  </div>
                </fieldset>

                <fieldset className="w-group" style={{ marginTop: 6 }}>
                  <legend>Rules (raw tag genre → bucket)</legend>
                  <div className="w-listwrap w-sunken" style={{ maxHeight: 150 }}>
                    <table className="w-table">
                      <tbody>
                        {map.rules.map((r, i) => (
                          <tr key={`${r.raw}|${r.substring}|${i}`}>
                            <td title={r.raw}>{r.raw}</td>
                            <td className="w-muted">{r.substring ? 'contains' : 'exact'}</td>
                            <td>→ {r.bucket}</td>
                            <td style={{ width: 30 }}>
                              <button className="w-btn" style={{ minHeight: 15, padding: '0 5px' }}
                                onClick={() => removeRule(i)}>✕</button>
                            </td>
                          </tr>
                        ))}
                        {map.rules.length === 0 && (
                          <tr><td className="w-muted" style={{ padding: 6 }}>No rules yet. A raw genre equal to a bucket name maps by itself; everything else lands in Unknown.</td></tr>
                        )}
                      </tbody>
                    </table>
                  </div>
                  <div className="w-toolbar" style={{ marginTop: 4, flexWrap: 'wrap' }}>
                    <input type="text" value={ruleRaw} style={{ flex: 1, minWidth: 120 }}
                      onChange={e => setRuleRaw(e.target.value)}
                      placeholder="Raw genre (e.g. Eurodance)" />
                    <label className="w-check" title="Match anywhere inside the raw genre instead of the whole value">
                      <input type="checkbox" checked={ruleSub} onChange={e => setRuleSub(e.target.checked)} /> contains
                    </label>
                    <select value={ruleBucket} onChange={e => setRuleBucket(e.target.value)}>
                      <option value="">→ bucket…</option>
                      {map.buckets.map(b => <option key={b} value={b}>{b}</option>)}
                    </select>
                    <button className="w-btn" disabled={!ruleRaw.trim() || !ruleBucket} onClick={addRule}>Add rule</button>
                  </div>
                </fieldset>
              </div>

              {/* Right: raw-genre worklist */}
              <fieldset className="w-group" style={{ width: 250, display: 'flex', flexDirection: 'column' }}>
                <legend>Raw genres in the library</legend>
                <div className="w-muted" style={{ marginBottom: 4 }}>
                  Click one to prefill a rule.{untagged > 0 ? ` ${untagged} track(s) have no genre tag.` : ''}
                </div>
                <div className="w-listwrap w-sunken" style={{ flex: 1, maxHeight: 290 }}>
                  <table className="w-table">
                    <tbody>
                      {raws.map(r => (
                        <tr key={r.raw} style={{ cursor: 'pointer' }}
                          title={`Currently → ${r.bucket}`}
                          onClick={() => { setRuleRaw(r.raw); setRuleSub(false) }}>
                          <td title={r.raw} style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{r.raw}</td>
                          <td className="w-num">{r.count}</td>
                          <td className={r.bucket === 'Unknown' ? 'w-err' : 'w-muted'}>{r.bucket}</td>
                        </tr>
                      ))}
                      {raws.length === 0 && (
                        <tr><td className="w-muted" style={{ padding: 6 }}>No raw genres (library empty or untagged).</td></tr>
                      )}
                    </tbody>
                  </table>
                </div>
              </fieldset>
            </div>
          )}

          <div className="w-toolbar" style={{ marginTop: 8 }}>
            <span className="w-muted">{dirty ? 'Unsaved changes — Save applies to the whole library instantly.' : ''}</span>
            <span style={{ flex: 1 }} />
            <button className="w-btn" disabled={!dirty || busy || !map} onClick={save}>Save</button>
            <button className="w-btn" onClick={onClose}>Close</button>
          </div>
        </div>
      </div>
    </div>
  )
}
