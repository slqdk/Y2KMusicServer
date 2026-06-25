import { useEffect, useMemo, useRef, useState, type MouseEvent as ReactMouseEvent } from 'react'
import * as api from './api'
import { fmtTime } from './api'
import type { ScanInfo, AnalysisInfo } from './useHub'
import CategoryDialog from './CategoryDialog'
import PropertiesDialog from './PropertiesDialog'
import ScanBar from './ScanBar'
import AnalyzeBar from './AnalyzeBar'

// The library loads in full and scrolls; this take covers any realistic library
// in one request (the server clamps it to its own ceiling).
const LOAD_ALL = 100000

// Right-click menu geometry, used only to keep it inside the viewport.
const MENU_W = 184
const MENU_H = 156

type RowMenu = { x: number; y: number; track: api.TrackDto }

export default function LibraryBrowser({ scan, analysis, onPlayNow }: { scan: ScanInfo | null; analysis: AnalysisInfo | null; onPlayNow: (trackId: number) => Promise<unknown> | void }) {
  const [cats, setCats] = useState<api.CategoryDto[]>([])
  const [catErr, setCatErr] = useState<string | null>(null)
  const [q, setQ] = useState('')
  const [categoryId, setCategoryId] = useState<number | null>(null)
  const [page, setPage] = useState<api.TracksPage | null>(null)
  const [selId, setSelId] = useState<number | null>(null)
  const [busyId, setBusyId] = useState<number | null>(null)
  const [dialogCat, setDialogCat] = useState<api.CategoryDto | null>(null)
  const [menu, setMenu] = useState<RowMenu | null>(null)
  const [propsId, setPropsId] = useState<number | null>(null)
  const debounce = useRef<number | undefined>(undefined)

  const refreshCats = () => api.getCategories().then(setCats).catch(() => {})
  useEffect(() => { refreshCats() }, [])

  const loadTracks = (qv: string, cat: number | null) =>
    api.getTracks({ q: qv, categoryId: cat, take: LOAD_ALL }).then(setPage).catch(() => setPage(null))

  // Debounced search; immediate on filter change.
  useEffect(() => {
    window.clearTimeout(debounce.current)
    debounce.current = window.setTimeout(() => loadTracks(q, categoryId), 250)
    return () => window.clearTimeout(debounce.current)
  }, [q, categoryId])

  // Dismiss the row menu on any click, scroll, resize, or Escape. The menu
  // item's own onClick runs first (it bubbles to the React root before this
  // window listener), so the action still fires.
  useEffect(() => {
    if (!menu) return
    const close = () => setMenu(null)
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setMenu(null) }
    window.addEventListener('click', close)
    window.addEventListener('resize', close)
    window.addEventListener('scroll', close, true)
    window.addEventListener('keydown', onKey)
    return () => {
      window.removeEventListener('click', close)
      window.removeEventListener('resize', close)
      window.removeEventListener('scroll', close, true)
      window.removeEventListener('keydown', onKey)
    }
  }, [menu])

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

  // Add the track to the playlist as a manual pick (it lands ahead of the
  // auto-fill). Double-click and the "Play as next song" menu item share this.
  const playNext = (id: number) => rowAct(id, () => api.addToPlaylist(id, 'Manual'))

  // Append the track to the very end of the playlist (still a manual pick).
  const addEnd = (id: number) => rowAct(id, () => api.addToPlaylist(id, 'Manual', true))

  // Crossfade to the track now (or load + play it if nothing is on air); the
  // parent owns the decision because it has the live playback status.
  const doPlayNow = (id: number) => rowAct(id, async () => { await onPlayNow(id) })

  // Re-read tags + re-measure BPM/LUFS for one track, then refresh the page so
  // the row shows the new values.
  const rescanOne = (id: number) => rowAct(id, async () => {
    await api.rescanTrack(id)
    await loadTracks(q, categoryId)
  })

  const openMenu = (e: ReactMouseEvent, t: api.TrackDto) => {
    e.preventDefault()
    setSelId(t.id)
    const x = Math.max(4, Math.min(e.clientX, window.innerWidth - MENU_W - 4))
    const y = Math.max(4, Math.min(e.clientY, window.innerHeight - MENU_H - 4))
    setMenu({ x, y, track: t })
  }

  const total = page?.total ?? 0
  const items = page?.items ?? []

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
                <button className="w-btn"
                  disabled={c.folderCount === 0 || (scan != null && (scan.state === 1 || scan.state === 2))}
                  onClick={e => { e.stopPropagation(); api.startScan(c.id).catch(() => {}) }}
                  title={c.folderCount === 0 ? 'No folder to rescan (⚙ add one)' : "Rescan this category's folder(s)"}>↻</button>
                <button className="w-btn" title="Folders, schedule, rename"
                  onClick={e => { e.stopPropagation(); setDialogCat(c) }}>⚙</button>
              </div>
            </div>
          )
        })}
      </div>
      {catErr && <div className="w-err" style={{ marginTop: 4 }}>{catErr}</div>}

      <ScanBar live={scan} onComplete={() => { refreshCats(); loadTracks(q, categoryId) }} />
      <AnalyzeBar live={analysis} onComplete={() => loadTracks(q, categoryId)} />

      {/* Search */}
      <div className="w-toolbar">
        <label>Search:</label>
        <input type="search" value={q} style={{ flex: 1 }}
          onChange={e => setQ(e.target.value)}
          placeholder="title / artist / album" />
        {categoryId != null && (
          <button className="w-btn" onClick={() => setCategoryId(null)}>
            Clear: {catName.get(categoryId) ?? categoryId}
          </button>
        )}
      </div>

      {/* Track table. Double-click a row to queue it as the next song; right-click
          for the full action menu. */}
      <div className="w-listwrap w-sunken" style={{ flex: 1, minHeight: 0, marginTop: 6 }}>
        <table className="w-table">
          <thead>
            <tr>
              <th>Title</th><th>Artist</th><th>Category</th>
              <th className="w-num">Dur</th><th className="w-num">BPM</th><th className="w-num">LUFS</th>
            </tr>
          </thead>
          <tbody>
            {items.map(t => (
              <tr key={t.id} className={selId === t.id ? 'w-rowsel' : ''}
                onClick={() => setSelId(t.id)}
                onDoubleClick={() => playNext(t.id)}
                onContextMenu={e => openMenu(e, t)}
                title="Double-click to queue as next · right-click for more">
                <td title={t.title ?? ''}>{t.title ?? '(untitled)'}{busyId === t.id ? ' ⟳' : ''}</td>
                <td title={t.artist ?? ''}>{t.artist ?? '---'}</td>
                <td>{t.categoryId != null ? (catName.get(t.categoryId) ?? '?') : '---'}</td>
                <td className="w-num">{fmtTime(t.durationSec)}</td>
                <td className="w-num">{t.bpm != null ? Math.round(t.bpm) : '---'}</td>
                <td className="w-num">{t.lufs != null ? t.lufs.toFixed(1) : '---'}</td>
              </tr>
            ))}
            {items.length === 0 && (
              <tr><td colSpan={6} className="w-muted" style={{ padding: 8 }}>No tracks. Assign a folder to a category; it scans and analyses automatically.</td></tr>
            )}
          </tbody>
        </table>
      </div>

      {menu && (
        <ul className="w-ctxmenu" role="menu" style={{ left: menu.x, top: menu.y }}
          onContextMenu={e => e.preventDefault()}>
          <li className="w-ctxitem" role="menuitem"
            onClick={() => { playNext(menu.track.id); setMenu(null) }}>Play as next song</li>
          <li className="w-ctxitem" role="menuitem"
            onClick={() => { addEnd(menu.track.id); setMenu(null) }}>Add to end of playlist</li>
          <li className="w-ctxitem" role="menuitem"
            onClick={() => { doPlayNow(menu.track.id); setMenu(null) }}>Play now</li>
          <li className="w-ctxsep" role="separator" />
          <li className="w-ctxitem" role="menuitem"
            onClick={() => { rescanOne(menu.track.id); setMenu(null) }}>Rescan this song</li>
          <li className="w-ctxitem" role="menuitem"
            onClick={() => { setPropsId(menu.track.id); setMenu(null) }}>Properties</li>
        </ul>
      )}

      {dialogCat && (
        <CategoryDialog
          category={dialogCat}
          onClose={() => setDialogCat(null)}
          onChanged={() => { refreshCats(); loadTracks(q, categoryId) }}
        />
      )}

      {propsId != null && (
        <PropertiesDialog trackId={propsId} onClose={() => setPropsId(null)} />
      )}
    </div>
  )
}
