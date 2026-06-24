import { useEffect, useMemo, useRef, useState } from 'react'
import * as api from './api'
import { fmtTime } from './api'
import type { ScanInfo, AnalysisInfo } from './useHub'
import CategoryDialog from './CategoryDialog'
import ScanBar from './ScanBar'
import AnalyzeBar from './AnalyzeBar'

const PAGE = 50

export default function LibraryBrowser({ scan, analysis, onCueB }: { scan: ScanInfo | null; analysis: AnalysisInfo | null; onCueB: (trackId: number) => Promise<unknown> }) {
  const [cats, setCats] = useState<api.CategoryDto[]>([])
  const [catErr, setCatErr] = useState<string | null>(null)
  const [q, setQ] = useState('')
  const [categoryId, setCategoryId] = useState<number | null>(null)
  const [skip, setSkip] = useState(0)
  const [page, setPage] = useState<api.TracksPage | null>(null)
  const [selId, setSelId] = useState<number | null>(null)
  const [busyId, setBusyId] = useState<number | null>(null)
  const [dialogCat, setDialogCat] = useState<api.CategoryDto | null>(null)
  const debounce = useRef<number | undefined>(undefined)

  const refreshCats = () => api.getCategories().then(setCats).catch(() => {})
  useEffect(() => { refreshCats() }, [])

  const loadTracks = (qv: string, cat: number | null, sk: number) =>
    api.getTracks({ q: qv, categoryId: cat, skip: sk, take: PAGE }).then(setPage).catch(() => setPage(null))

  // Debounced search; immediate on filter/page change.
  useEffect(() => {
    window.clearTimeout(debounce.current)
    debounce.current = window.setTimeout(() => loadTracks(q, categoryId, skip), 250)
    return () => window.clearTimeout(debounce.current)
  }, [q, categoryId, skip])

  const catName = useMemo(() => {
    const m = new Map<number, string>()
    cats.forEach(c => m.set(c.id, c.name))
    return m
  }, [cats])

  const toggleEnable = async (c: api.CategoryDto) => {
    setCatErr(null)
    try { await api.setCategoryEnabled(c.id, !c.enabled); await refreshCats() }
    catch (e) {
      setCatErr(e instanceof api.ApiError && e.status === 422
        ? `"${c.name}" needs a folder before it can be enabled.`
        : `Could not toggle "${c.name}".`)
    }
  }

  const rowAct = async (id: number, fn: () => Promise<unknown>) => {
    setBusyId(id)
    try { await fn() } catch { /* ignore */ } finally { setBusyId(null) }
  }

  const total = page?.total ?? 0
  const items = page?.items ?? []
  const from = total === 0 ? 0 : skip + 1
  const to = Math.min(skip + PAGE, total)

  return (
    <div className="w-panel w-raised w-libpanel">
      <div className="w-panelhead">Library {page && <span style={{ fontWeight: 'normal' }}>— {total} tracks</span>}</div>

      {/* Categories */}
      <div className="w-cats">
        {cats.map(c => {
          const on = c.enabled
          const active = categoryId === c.id
          return (
            <div key={c.id}
              className={`w-cat w-raised ${on ? 'w-on' : 'w-off'} ${active ? 'w-active' : ''}`}
              onClick={() => setCategoryId(active ? null : c.id)}
              title={active ? 'Click to clear filter' : 'Click to filter library by this category'}>
              <div className="w-cat-name">{c.name}</div>
              <div className="w-cat-count">{c.trackCount} tracks</div>
              <div style={{ display: 'flex', gap: 2 }}>
                <button className="w-btn" style={{ flex: 1 }} disabled={!on && c.folderCount === 0}
                  onClick={e => { e.stopPropagation(); toggleEnable(c) }}
                  title={!on && c.folderCount === 0 ? 'Add a folder first (⚙ Settings)' : ''}>
                  {on ? 'Disable' : 'Enable'}
                </button>
                <button className="w-btn" title="Folders, schedule, rename"
                  onClick={e => { e.stopPropagation(); setDialogCat(c) }}>⚙</button>
              </div>
            </div>
          )
        })}
      </div>
      {catErr && <div className="w-err" style={{ marginTop: 4 }}>{catErr}</div>}

      <ScanBar live={scan} onComplete={() => { refreshCats(); loadTracks(q, categoryId, skip) }} />
      <AnalyzeBar live={analysis} onComplete={() => loadTracks(q, categoryId, skip)} />

      {/* Search */}
      <div className="w-toolbar">
        <label>Search:</label>
        <input type="search" value={q} style={{ flex: 1 }}
          onChange={e => { setSkip(0); setQ(e.target.value) }}
          placeholder="title / artist / album" />
        {categoryId != null && (
          <button className="w-btn" onClick={() => setCategoryId(null)}>
            Clear: {catName.get(categoryId) ?? categoryId}
          </button>
        )}
      </div>

      {/* Track table */}
      <div className="w-listwrap w-sunken" style={{ flex: 1, minHeight: 0, marginTop: 6 }}>
        <table className="w-table">
          <thead>
            <tr>
              <th>Title</th><th>Artist</th><th>Category</th>
              <th className="w-num">Dur</th><th className="w-num">BPM</th><th className="w-num">LUFS</th><th></th>
            </tr>
          </thead>
          <tbody>
            {items.map(t => (
              <tr key={t.id} className={selId === t.id ? 'w-rowsel' : ''} onClick={() => setSelId(t.id)}>
                <td title={t.title ?? ''}>{t.title ?? '(untitled)'}</td>
                <td title={t.artist ?? ''}>{t.artist ?? '---'}</td>
                <td>{t.categoryId != null ? (catName.get(t.categoryId) ?? '?') : '---'}</td>
                <td className="w-num">{fmtTime(t.durationSec)}</td>
                <td className="w-num">{t.bpm != null ? Math.round(t.bpm) : '---'}</td>
                <td className="w-num">{t.lufs != null ? t.lufs.toFixed(1) : '---'}</td>
                <td className="w-rowbtns" onClick={e => e.stopPropagation()}>
                  <button className="w-btn" disabled={busyId === t.id}
                    title="Load + play now"
                    onClick={() => rowAct(t.id, async () => { await api.load(t.id); await api.play() })}>▶</button>
                  <button className="w-btn" disabled={busyId === t.id}
                    title="Add to playlist"
                    onClick={() => rowAct(t.id, () => api.addToPlaylist(t.id, 'Manual'))}>+</button>
                  <button className="w-btn" disabled={busyId === t.id}
                    title="Cue to Deck B (starts silent for beat-matching)"
                    onClick={() => rowAct(t.id, () => onCueB(t.id))}>→B</button>
                </td>
              </tr>
            ))}
            {items.length === 0 && (
              <tr><td colSpan={7} className="w-muted" style={{ padding: 8 }}>No tracks. Assign a folder to a category; it scans and analyses automatically.</td></tr>
            )}
          </tbody>
        </table>
      </div>

      {/* Paging */}
      <div className="w-toolbar">
        <button className="w-btn" disabled={skip === 0} onClick={() => setSkip(Math.max(0, skip - PAGE))}>‹ Prev</button>
        <button className="w-btn" disabled={to >= total} onClick={() => setSkip(skip + PAGE)}>Next ›</button>
        <span className="w-spacer" />
        <span className="w-muted">{from}–{to} of {total}</span>
      </div>

      {dialogCat && (
        <CategoryDialog
          category={dialogCat}
          onClose={() => setDialogCat(null)}
          onChanged={() => { refreshCats(); loadTracks(q, categoryId, skip) }}
        />
      )}
    </div>
  )
}
