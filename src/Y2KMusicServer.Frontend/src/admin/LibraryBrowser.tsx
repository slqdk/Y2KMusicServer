import { useEffect, useRef, useState, type MouseEvent as ReactMouseEvent } from 'react'
import * as api from './api'
import { fmtTime } from './api'
import type { ScanInfo, AnalysisInfo } from './useHub'
import PropertiesDialog from './PropertiesDialog'
import FoldersDialog from './FoldersDialog'
import GenreMapDialog from './GenreMapDialog'
import ScanBar from './ScanBar'
import AnalyzeBar from './AnalyzeBar'
import { useColumnWidths, ColResizer } from './useColumns'

// The library loads in full and scrolls; this take covers any realistic library
// in one request (the server clamps it to its own ceiling).
const LOAD_ALL = 100000

// Right-click menu geometry, used only to keep it inside the viewport. The
// height grows with the saved-playlist list appended under "Add to playlist".
const MENU_W = 200
const MENU_BASE_H = 198
const MENU_ITEM_H = 22

type RowMenu = { x: number; y: number; track: api.TrackDto }

const decadeLabel = (d: number) => (d === 0 ? 'Unknown' : `${d}s`)

export default function LibraryBrowser({ scan, analysis, onPlayNow }: { scan: ScanInfo | null; analysis: AnalysisInfo | null; onPlayNow: (trackId: number) => Promise<unknown> | void }) {
  const [facets, setFacets] = useState<api.FacetsDto | null>(null)
  const [playlists, setPlaylists] = useState<api.SavedPlaylistDto[]>([])
  const [q, setQ] = useState('')
  const [format, setFormat] = useState<string | null>(null)
  const [genre, setGenre] = useState<string | null>(null)
  const [decade, setDecade] = useState<number | null>(null)
  const [page, setPage] = useState<api.TracksPage | null>(null)
  const [selId, setSelId] = useState<number | null>(null)
  const [busyId, setBusyId] = useState<number | null>(null)
  const [menu, setMenu] = useState<RowMenu | null>(null)
  const [propsId, setPropsId] = useState<number | null>(null)
  const [foldersOpen, setFoldersOpen] = useState(false)
  const [genreMapOpen, setGenreMapOpen] = useState(false)
  const [note, setNote] = useState<string | null>(null)
  const [preview, setPreview] = useState<api.TrackDto | null>(null)
  const debounce = useRef<number | undefined>(undefined)

  // Resizable, fixed-width columns:
  // Title, Artist, Genre, Decade, Type, Dur, BPM, LUFS.
  // New storage key — the old 6-column widths don't fit this table.
  const { colgroup, startResize } = useColumnWidths('y2k.cols.library2', [23, 19, 17, 11, 6, 5, 6, 6, 7])

  const refreshFacets = () => api.getFacets().then(setFacets).catch(() => {})
  const refreshPlaylists = () =>
    api.getSavedPlaylists().then(r => setPlaylists(r.playlists)).catch(() => {})
  useEffect(() => { refreshFacets(); refreshPlaylists() }, [])

  const loadTracks = (qv: string, f: string | null, g: string | null, d: number | null) =>
    api.getTracks({ q: qv, format: f, genre: g, decade: d, take: LOAD_ALL })
      .then(setPage).catch(() => setPage(null))

  // Debounced search; immediate on filter change.
  useEffect(() => {
    window.clearTimeout(debounce.current)
    debounce.current = window.setTimeout(() => loadTracks(q, format, genre, decade), 250)
    return () => window.clearTimeout(debounce.current)
  }, [q, format, genre, decade])

  const refreshAll = () => { refreshFacets(); refreshPlaylists(); loadTracks(q, format, genre, decade) }

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

  // Auto-clear the transient "added to X" note.
  useEffect(() => {
    if (!note) return
    const id = window.setTimeout(() => setNote(null), 2500)
    return () => window.clearTimeout(id)
  }, [note])

  const rowAct = async (id: number, fn: () => Promise<unknown>) => {
    setBusyId(id)
    try { await fn() } catch { /* ignore */ } finally { setBusyId(null) }
  }

  // Add the track to the live queue as a manual pick (it lands ahead of the
  // auto-fill). Double-click and the "Play as next song" menu item share this.
  const playNext = (id: number) => rowAct(id, () => api.addToPlaylist(id, 'Manual'))

  // Append the track to the very end of the live queue (still a manual pick).
  const addEnd = (id: number) => rowAct(id, () => api.addToPlaylist(id, 'Manual', true))

  // Crossfade to the track now (or load + play it if nothing is on air); the
  // parent owns the decision because it has the live playback status.
  const doPlayNow = (id: number) => rowAct(id, async () => { await onPlayNow(id) })

  // Re-read tags + re-measure BPM/LUFS for one track, then refresh the page so
  // the row shows the new values.
  const rescanOne = (id: number) => rowAct(id, async () => {
    await api.rescanTrack(id)
    refreshAll()
  })

  // Add the track to a saved playlist (dup-tolerant server-side).
  const addToSaved = (trackId: number, pl: api.SavedPlaylistDto) => rowAct(trackId, async () => {
    const r = await api.addToSavedPlaylist(pl.id, trackId)
    setNote(r.added ? `Added to "${pl.name}".` : `Already in "${pl.name}".`)
    refreshPlaylists()
  })

  const openMenu = (e: ReactMouseEvent, t: api.TrackDto) => {
    e.preventDefault()
    setSelId(t.id)
    const h = MENU_BASE_H + Math.max(1, playlists.length) * MENU_ITEM_H
    const x = Math.max(4, Math.min(e.clientX, window.innerWidth - MENU_W - 4))
    const y = Math.max(4, Math.min(e.clientY, window.innerHeight - h - 4))
    setMenu({ x, y, track: t })
  }

  const total = page?.total ?? 0
  const items = page?.items ?? []
  const filtered = format != null || genre != null || decade != null || q.trim() !== ''

  return (
    <div className="w-panel w-raised w-libpanel">
      <div className="w-panelhead">Library {page && <span style={{ fontWeight: 'normal' }}>— {total} tracks{filtered ? ' (filtered)' : ''}</span>}</div>

      {/* Filter bar: Format / Genre / Decade facets + free-text search. */}
      <div className="w-toolbar" style={{ flexWrap: 'wrap' }}>
        <label>Format:</label>
        <select value={format ?? ''} onChange={e => setFormat(e.target.value || null)}>
          <option value="">All</option>
          {facets?.formats.map(f => (
            <option key={f.name} value={f.name}>{f.name} ({f.count})</option>
          ))}
        </select>
        <label>Genre:</label>
        <select value={genre ?? ''} onChange={e => setGenre(e.target.value || null)}>
          <option value="">All</option>
          {facets?.genres.map(g => (
            <option key={g.name} value={g.name}>{g.name} ({g.count})</option>
          ))}
        </select>
        <label>Decade:</label>
        <select value={decade == null ? '' : String(decade)}
          onChange={e => setDecade(e.target.value === '' ? null : Number(e.target.value))}>
          <option value="">All</option>
          {facets?.decades.map(d => (
            <option key={d.decade} value={d.decade}>{decadeLabel(d.decade)} ({d.count})</option>
          ))}
        </select>
        <span className="w-spacer" style={{ flex: 1 }} />
        <button className="w-btn" onClick={() => setFoldersOpen(true)} title="The folders the library scans">Folders…</button>
        <button className="w-btn" onClick={() => setGenreMapOpen(true)} title="Map raw tag genres onto your genre buckets">Genre map…</button>
      </div>

      <ScanBar live={scan} onComplete={refreshAll} />
      <AnalyzeBar live={analysis} onComplete={() => loadTracks(q, format, genre, decade)} />

      {/* Search */}
      <div className="w-toolbar">
        <label>Search:</label>
        <input type="search" value={q} style={{ flex: 1 }}
          onChange={e => setQ(e.target.value)}
          placeholder="title / artist / album" />
        {filtered && (
          <button className="w-btn"
            onClick={() => { setQ(''); setFormat(null); setGenre(null); setDecade(null) }}>
            Clear filters
          </button>
        )}
        {note && <span className="w-muted">{note}</span>}
      </div>

      {/* Preview player: browser-side listen via the raw-audio endpoint. Never
          touches the decks or /stream; audible on the machine running this
          admin page. keys the <audio> by track id so switching tracks reloads. */}
      {preview && (
        <div className="w-toolbar w-previewbar" style={{ marginTop: 6 }}>
          <span style={{ fontWeight: 'bold', whiteSpace: 'nowrap' }}>► Preview:</span>
          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
            title={`${preview.title ?? ''} — ${preview.artist ?? ''}`}>
            {preview.title ?? '(untitled)'} — {preview.artist ?? '---'}
          </span>
          <audio key={preview.id} controls autoPlay src={api.trackAudioUrl(preview.id)}
            style={{ flex: 1, minWidth: 160, height: 24 }} />
          <button className="w-btn" title="Stop and close the preview"
            onClick={() => setPreview(null)}>✕</button>
        </div>
      )}

      {/* Track table. Double-click a row to queue it as the next song; right-click
          for the full action menu (including Add to playlist). */}
      <div className="w-listwrap w-sunken" style={{ flex: 1, minHeight: 0, marginTop: 6, overflowX: 'hidden' }}>
        <table className="w-table w-grid">
          {colgroup}
          <thead>
            <tr>
              <th>Title<ColResizer onMouseDown={startResize(0)} /></th>
              <th>Artist<ColResizer onMouseDown={startResize(1)} /></th>
              <th>Album<ColResizer onMouseDown={startResize(2)} /></th>
              <th>Genre<ColResizer onMouseDown={startResize(3)} /></th>
              <th>Decade<ColResizer onMouseDown={startResize(4)} /></th>
              <th>Type<ColResizer onMouseDown={startResize(5)} /></th>
              <th className="w-num">Dur<ColResizer onMouseDown={startResize(6)} /></th>
              <th className="w-num">BPM<ColResizer onMouseDown={startResize(7)} /></th>
              <th className="w-num">LUFS</th>
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
                <td title={t.album ?? ''}>{t.album ?? '---'}</td>
                <td title={t.rawGenre
                  ? (t.genreBucket === 'Unknown'
                    ? `Unmapped tag genre "${t.rawGenre}" — add a rule in Genre map…`
                    : `Tag genre: ${t.rawGenre}`)
                  : 'No genre tag'}>
                  {t.genreBucket !== 'Unknown'
                    ? t.genreBucket
                    : t.rawGenre && t.rawGenre.trim().toLowerCase() !== 'unknown'
                      ? <span className="w-muted">{t.rawGenre}</span>
                      : 'Unknown'}
                </td>
                <td>{t.decade != null ? decadeLabel(t.decade) : '---'}</td>
                <td>{t.type ?? '---'}</td>
                <td className="w-num">{fmtTime(t.durationSec)}</td>
                <td className="w-num">{t.bpm != null ? Math.round(t.bpm) : '---'}</td>
                <td className="w-num">{t.lufs != null ? t.lufs.toFixed(1) : '---'}</td>
              </tr>
            ))}
            {items.length === 0 && (
              <tr><td colSpan={9} className="w-muted" style={{ padding: 8 }}>No tracks. Add a music folder (Folders…); it scans and analyses automatically.</td></tr>
            )}
          </tbody>
        </table>
      </div>

      {menu && (
        <ul className="w-ctxmenu" role="menu" style={{ left: menu.x, top: menu.y, minWidth: MENU_W }}
          onContextMenu={e => e.preventDefault()}>
          <li className="w-ctxitem" role="menuitem"
            onClick={() => { playNext(menu.track.id); setMenu(null) }}>Play as next song</li>
          <li className="w-ctxitem" role="menuitem"
            onClick={() => { addEnd(menu.track.id); setMenu(null) }}>Add to end of queue</li>
          <li className="w-ctxitem" role="menuitem"
            onClick={() => { doPlayNow(menu.track.id); setMenu(null) }}>Play now</li>
          <li className="w-ctxitem" role="menuitem"
            title="Listen here in the admin — the decks, queue and stream are untouched"
            onClick={() => { setPreview(menu.track); setMenu(null) }}>Preview (listen here)</li>
          <li className="w-ctxsep" role="separator" />
          <li className="w-ctxhead" aria-hidden="true">Add to playlist</li>
          {playlists.length === 0 && (
            <li className="w-ctxitem w-ctxdisabled">(no playlists yet)</li>
          )}
          {playlists.map(pl => (
            <li key={pl.id} className="w-ctxitem" role="menuitem" style={{ paddingLeft: 18 }}
              onClick={() => { addToSaved(menu.track.id, pl); setMenu(null) }}>
              {pl.name} <span className="w-muted">({pl.trackCount})</span>
            </li>
          ))}
          <li className="w-ctxsep" role="separator" />
          <li className="w-ctxitem" role="menuitem"
            onClick={() => { rescanOne(menu.track.id); setMenu(null) }}>Rescan this song</li>
          <li className="w-ctxitem" role="menuitem"
            onClick={() => { setPropsId(menu.track.id); setMenu(null) }}>Properties</li>
        </ul>
      )}

      {foldersOpen && (
        <FoldersDialog onClose={() => setFoldersOpen(false)} onChanged={refreshAll} />
      )}

      {genreMapOpen && (
        <GenreMapDialog onClose={() => setGenreMapOpen(false)} onChanged={refreshAll} />
      )}

      {propsId != null && (
        <PropertiesDialog trackId={propsId} onClose={() => setPropsId(null)} onChanged={refreshAll} />
      )}
    </div>
  )
}
