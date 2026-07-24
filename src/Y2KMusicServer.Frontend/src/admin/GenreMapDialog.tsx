import { useEffect, useMemo, useState } from 'react'
import * as api from './api'

/**
 * The genre-map editor. The worklist (every distinct raw tag genre with its
 * track count) is the main pane: each row carries an inline bucket selector, so
 * mapping a raw genre is ONE action — pick the bucket, and an exact rule is
 * staged. The selector also offers "New bucket …" which creates a bucket named
 * after the raw and maps to it. Everything stages locally and applies on Save
 * (query-time map — the whole library re-buckets instantly, no rescan).
 */
export default function GenreMapDialog({ onClose, onChanged }:
  { onClose: () => void; onChanged: () => void }) {

  const [map, setMap] = useState<api.GenreMap | null>(null)
  const [raws, setRaws] = useState<api.RawGenre[]>([])
  const [untagged, setUntagged] = useState(0)
  const [onlyUnmapped, setOnlyUnmapped] = useState(true)
  const [newBucket, setNewBucket] = useState('')
  const [busy, setBusy] = useState(false)
  const [dirty, setDirty] = useState(false)
  const [err, setErr] = useState<string | null>(null)
  const [lookup, setLookup] = useState<api.GenreLookupStatus | null>(null)

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

  // Online-lookup status: poll while the dialog is open; refresh the worklist
  // and the library behind the dialog when a pass finishes.
  useEffect(() => {
    let last = false
    const tick = () => api.getGenreLookupStatus().then(s => {
      setLookup(s)
      if (last && !s.running) { refreshRaws(); onChanged() }
      last = s.running
    }).catch(() => {})
    tick()
    const id = window.setInterval(tick, 1000)
    return () => window.clearInterval(id)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const startLookup = () => api.startGenreLookup().then(setLookup).catch(() => {})
  const stopLookup = () => api.stopGenreLookup().then(setLookup).catch(() => {})

  const edit = (m: api.GenreMap) => { setMap(m); setDirty(true) }

  // The bucket a raw genre resolves to under the STAGED (unsaved) map — exact
  // rule first, then a bucket with the same name; Unknown otherwise. Keeps the
  // worklist live while mapping, without waiting for Save.
  const stagedBucket = (raw: string): string => {
    if (!map) return 'Unknown'
    const rule = map.rules.find(r => !r.substring && r.raw.toLowerCase() === raw.toLowerCase())
    if (rule) {
      const b = map.buckets.find(x => x.toLowerCase() === rule.bucket.toLowerCase())
      return b ?? 'Unknown'
    }
    const direct = map.buckets.find(x => x.toLowerCase() === raw.toLowerCase())
    return direct ?? 'Unknown'
  }

  // ONE action per row: choose a bucket → stage an exact rule for the raw
  // (replacing any earlier exact rule). '' clears the mapping; NEW creates a
  // bucket named after the raw itself and maps to it.
  const NEW = '\u0000new'
  const mapRaw = (raw: string, choice: string) => {
    if (!map) return
    const rest = map.rules.filter(r => !(!r.substring && r.raw.toLowerCase() === raw.toLowerCase()))
    if (choice === '') {
      edit({ ...map, rules: rest })
      return
    }
    if (choice === NEW) {
      const name = raw.trim()
      const buckets = map.buckets.some(b => b.toLowerCase() === name.toLowerCase())
        ? map.buckets : [...map.buckets, name]
      // The bucket itself matches the raw by name — no rule needed.
      edit({ buckets, rules: rest })
      return
    }
    edit({ ...map, rules: [...rest, { raw, substring: false, bucket: choice }] })
  }

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

  const visibleRaws = useMemo(
    () => onlyUnmapped ? raws.filter(r => stagedBucket(r.raw) === 'Unknown') : raws,
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [raws, onlyUnmapped, map])

  const unmappedCount = useMemo(
    () => raws.filter(r => stagedBucket(r.raw) === 'Unknown').length,
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [raws, map])

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
        style={{ width: 860, maxWidth: '96vw' }}>
        <div className="w-titlebar">
          <span className="w-app">Genre map</span>
          <span style={{ flex: 1 }} />
          <button className="w-btn" onClick={onClose} style={{ minHeight: 16, padding: '0 7px' }}>✕</button>
        </div>

        <div className="w-dialog-body">
          {err && <div className="w-err" style={{ marginBottom: 4 }}>{err}</div>}
          {!map && !err && <div className="w-muted" style={{ padding: 4 }}>Loading…</div>}

          {map && (
            <>
              {/* Buckets: the filter values the library exposes. */}
              <fieldset className="w-group">
                <legend>Genre buckets (the library filter)</legend>
                <div className="w-toolbar" style={{ flexWrap: 'wrap', rowGap: 4 }}>
                  {map.buckets.map(b => (
                    <span key={b} className="w-raised" style={{ padding: '1px 4px', whiteSpace: 'nowrap' }}>
                      {b}{' '}
                      <button className="w-btn" style={{ minHeight: 14, padding: '0 4px' }}
                        title={`Remove "${b}" (its mappings go too; those tracks fall back to Unknown)`}
                        onClick={() => removeBucket(b)}>✕</button>
                    </span>
                  ))}
                  <span className="w-muted">+ Unknown (always)</span>
                  <span style={{ flex: 1 }} />
                  <input type="text" value={newBucket} style={{ width: 160 }}
                    onChange={e => setNewBucket(e.target.value)}
                    onKeyDown={e => { if (e.key === 'Enter') addBucket() }}
                    placeholder="New bucket…" />
                  <button className="w-btn" disabled={!newBucket.trim()} onClick={addBucket}>Add</button>
                </div>
              </fieldset>

              {/* The worklist IS the mapper: pick a bucket per raw genre. */}
              <fieldset className="w-group" style={{ marginTop: 6 }}>
                <legend>Raw tag genres → bucket</legend>
                <div className="w-toolbar">
                  <span className="w-muted">
                    {raws.length} raw genres, {unmappedCount} unmapped{untagged > 0 ? ` · ${untagged} track(s) with no tag at all` : ''}.
                    Multi-genre tags ("Rock, Latin, Funk") auto-match on their parts.
                  </span>
                  <span style={{ flex: 1 }} />
                  <label className="w-check">
                    <input type="checkbox" checked={onlyUnmapped}
                      onChange={e => setOnlyUnmapped(e.target.checked)} /> only unmapped
                  </label>
                </div>
                <div className="w-listwrap w-sunken" style={{ height: '46vh', minHeight: 220, marginTop: 4 }}>
                  <table className="w-table">
                    <thead>
                      <tr><th>Raw tag genre</th><th className="w-num">Tracks</th><th style={{ width: 200 }}>Bucket</th></tr>
                    </thead>
                    <tbody>
                      {visibleRaws.map(r => {
                        const cur = stagedBucket(r.raw)
                        return (
                          <tr key={r.raw}>
                            <td title={r.raw} style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{r.raw}</td>
                            <td className="w-num">{r.count}</td>
                            <td>
                              <select value={cur === 'Unknown' ? '' : cur} style={{ width: '100%' }}
                                onChange={e => mapRaw(r.raw, e.target.value)}>
                                <option value="">Unknown</option>
                                {map.buckets.map(b => <option key={b} value={b}>{b}</option>)}
                                <option value={NEW}>➕ New bucket “{r.raw}”</option>
                              </select>
                            </td>
                          </tr>
                        )
                      })}
                      {visibleRaws.length === 0 && (
                        <tr><td colSpan={3} className="w-muted" style={{ padding: 8 }}>
                          {onlyUnmapped ? 'Everything is mapped. 🎉 Untick "only unmapped" to review.' : 'No raw genres (library empty or untagged).'}
                        </td></tr>
                      )}
                    </tbody>
                  </table>
                </div>
              </fieldset>

              {/* Advanced rules (substring matches etc.) stay editable. */}
              {map.rules.some(r => r.substring) && (
                <fieldset className="w-group" style={{ marginTop: 6 }}>
                  <legend>Contains-rules</legend>
                  {map.rules.filter(r => r.substring).map((r, i) => (
                    <div key={`${r.raw}|${i}`} className="w-toolbar">
                      <span>“…{r.raw}…” → {r.bucket}</span>
                      <button className="w-btn" style={{ minHeight: 15, padding: '0 5px' }}
                        onClick={() => edit({ ...map, rules: map.rules.filter(x => x !== r) })}>✕</button>
                    </div>
                  ))}
                </fieldset>
              )}

              <fieldset className="w-group" style={{ marginTop: 6 }}>
                <legend>Online lookup (Deezer)</legend>
                <div className="w-toolbar" style={{ flexWrap: 'wrap' }}>
                  {!lookup?.running ? (
                    <button className="w-btn" onClick={startLookup}
                      title="Search Deezer for tracks with no genre tag / no album and fill the blanks in the library (files are never modified)">
                      Look up missing genres online
                    </button>
                  ) : (
                    <button className="w-btn" onClick={stopLookup}>Stop</button>
                  )}
                  <span className="w-muted" style={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                    {lookup?.running
                      ? `${lookup.processed}/${lookup.total} — ${lookup.found} found, ${lookup.misses} misses · ${lookup.currentTrack ?? ''}`
                      : lookup?.message ?? 'Fills tracks that have no genre tag; found genres appear as raw genres above for you to map.'}
                  </span>
                </div>
              </fieldset>

              <div className="w-toolbar" style={{ marginTop: 8 }}>
                <span className="w-muted">{dirty ? 'Unsaved changes — Save applies to the whole library instantly.' : ''}</span>
                <span style={{ flex: 1 }} />
                <button className="w-btn" disabled={!dirty || busy} onClick={save}>Save</button>
                <button className="w-btn" onClick={onClose}>Close</button>
              </div>
            </>
          )}
        </div>
      </div>
    </div>
  )
}
