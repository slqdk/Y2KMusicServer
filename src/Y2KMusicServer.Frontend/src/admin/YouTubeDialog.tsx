import { useEffect, useState } from 'react'
import * as api from './api'

// "Add from YouTube": search YouTube Music, then Queue or Play-now a result.
// The chosen track is downloaded into the local cache and indexed as a normal
// library track (a few seconds), then queued/played through the usual engine.
export default function YouTubeDialog(
  { onClose, onPlayNow }: { onClose: () => void; onPlayNow: (trackId: number) => void }
) {
  const [enabled, setEnabled] = useState<boolean | null>(null)
  const [q, setQ] = useState('')
  const [searching, setSearching] = useState(false)
  const [results, setResults] = useState<api.YouTubeSearchItem[]>([])
  const [searchErr, setSearchErr] = useState<string | null>(null)
  const [busyId, setBusyId] = useState<string | null>(null)   // one fetch at a time
  const [rowMsg, setRowMsg] = useState<Record<string, string>>({})

  useEffect(() => {
    api.getYouTubeSettings().then(s => setEnabled(s.enabled)).catch(() => setEnabled(false))
  }, [])

  const doSearch = async () => {
    const query = q.trim()
    if (!query || searching) return
    setSearching(true); setSearchErr(null); setResults([]); setRowMsg({})
    try { setResults(await api.searchYouTube(query, 12)) }
    catch { setSearchErr('Search failed. Is the tool stack installed? Run the check in Settings.') }
    finally { setSearching(false) }
  }

  // Fetch (download + index) the chosen result, then either queue it at the end
  // or play it now. Serialised via busyId so we don't launch parallel downloads.
  const act = async (item: api.YouTubeSearchItem, mode: 'queue' | 'play') => {
    if (busyId) return
    setBusyId(item.id)
    setRowMsg(m => ({ ...m, [item.id]: 'Fetching…' }))
    try {
      const r = await api.fetchYouTube(item.id)
      if (!r.ok || r.trackId == null) {
        setRowMsg(m => ({ ...m, [item.id]: r.error ? `Failed: ${r.error}` : 'Failed' }))
        return
      }
      if (mode === 'queue') {
        await api.addToPlaylist(r.trackId, 'Manual', true)
        setRowMsg(m => ({ ...m, [item.id]: r.alreadyCached ? 'Queued (cached)' : 'Queued' }))
      } else {
        onPlayNow(r.trackId)
        setRowMsg(m => ({ ...m, [item.id]: 'Playing' }))
      }
    } catch {
      setRowMsg(m => ({ ...m, [item.id]: 'Failed' }))
    } finally {
      setBusyId(null)
    }
  }

  return (
    <div className="w-overlay" onMouseDown={onClose}>
      <div className="w-dialog w-raised" onMouseDown={e => e.stopPropagation()}
        style={{ width: 560, maxWidth: '94vw' }}>
        <div className="w-titlebar">
          <span className="w-app">Add from YouTube</span>
          <span style={{ flex: 1 }} />
          <button className="w-btn" onClick={onClose} style={{ minHeight: 16, padding: '0 7px' }}>✕</button>
        </div>

        <div className="w-dialog-body">
          {enabled === null && <div className="w-muted">Loading…</div>}

          {enabled === false && (
            <div className="w-muted">
              YouTube integration is off. Turn it on in Settings → YouTube integration
              (and run the check there first to confirm the tool stack works).
            </div>
          )}

          {enabled === true && (
            <>
              <div className="w-toolbar">
                <input type="text" value={q} placeholder="Search YouTube Music…"
                  style={{ flex: 1 }} disabled={searching}
                  onChange={e => setQ(e.target.value)}
                  onKeyDown={e => { if (e.key === 'Enter') doSearch() }} />
                <button className="w-btn w-primary" disabled={searching || !q.trim()} onClick={doSearch}>
                  {searching ? 'Searching…' : 'Search'}
                </button>
              </div>
              {searchErr && <div className="w-err" style={{ marginTop: 4 }}>{searchErr}</div>}

              {results.length > 0 && (
                <div className="w-yt-results">
                  {results.map(item => (
                    <div className="w-yt-row" key={item.id}>
                      <div className="w-yt-info">
                        <div className="w-yt-title">{item.title}</div>
                        <div className="w-yt-meta">
                          {(item.artist ?? 'Unknown') + ' · ' + api.fmtTime(item.durationSec)}
                        </div>
                      </div>
                      {rowMsg[item.id] && <span className="w-yt-rowmsg">{rowMsg[item.id]}</span>}
                      <div className="w-yt-actions">
                        <button className="w-btn" disabled={busyId !== null}
                          onClick={() => act(item, 'queue')}>Queue</button>
                        <button className="w-btn" disabled={busyId !== null}
                          onClick={() => act(item, 'play')}>Play now</button>
                      </div>
                    </div>
                  ))}
                </div>
              )}

              {!searching && !searchErr && results.length === 0 && (
                <div className="w-muted" style={{ marginTop: 6 }}>
                  Search for a track, then Queue it or Play it now. Fetching downloads the
                  audio to the local cache (a few seconds) before it plays.
                </div>
              )}
            </>
          )}
        </div>
      </div>
    </div>
  )
}
