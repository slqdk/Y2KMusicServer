import { useCallback, useEffect, useRef, useState } from 'react'
import * as api from './api'

/**
 * "Add from YouTube" — two ways in, landing in different places on purpose:
 *
 *  - Paste links (one per line). Each becomes a background download job: the
 *    audio is fetched at best quality, tags and cover art are embedded, the file
 *    is filed as "Artist - Title.mp3" in the YouTube folder and indexed as an
 *    ordinary library track. Album / playlist links expand to their tracks;
 *    plain words take the first hit, so you don't have to go and find a URL.
 *  - Search, then Queue / Play now. That path fetches into the local web cache
 *    for immediate play rather than filing it in the YouTube folder.
 *
 * Jobs are polled while the dialog is open; closing it doesn't stop anything —
 * the queue is drained server-side.
 */
export default function YouTubeDialog(
  { onClose, onPlayNow }: { onClose: () => void; onPlayNow: (trackId: number) => void }
) {
  const [enabled, setEnabled] = useState<boolean | null>(null)

  // Downloads
  const [urls, setUrls] = useState('')
  const [queueing, setQueueing] = useState(false)
  const [dlErr, setDlErr] = useState<string | null>(null)
  const [dl, setDl] = useState<api.YouTubeDownloads | null>(null)

  // Search
  const [q, setQ] = useState('')
  const [searching, setSearching] = useState(false)
  const [results, setResults] = useState<api.YouTubeSearchItem[]>([])
  const [searchErr, setSearchErr] = useState<string | null>(null)
  const [busyId, setBusyId] = useState<string | null>(null)   // one fetch at a time
  const [rowMsg, setRowMsg] = useState<Record<string, string>>({})

  const alive = useRef(true)
  useEffect(() => () => { alive.current = false }, [])

  useEffect(() => {
    api.getYouTubeSettings().then(s => setEnabled(s.enabled)).catch(() => setEnabled(false))
  }, [])

  const refreshJobs = useCallback(() => {
    api.getYouTubeDownloads().then(d => { if (alive.current) setDl(d) }).catch(() => {})
  }, [])

  // Poll while open. 1.5 s is fast enough to watch a percentage climb without
  // flooding the server.
  useEffect(() => {
    refreshJobs()
    const id = window.setInterval(refreshJobs, 1500)
    return () => window.clearInterval(id)
  }, [refreshJobs])

  const queueDownloads = async () => {
    const text = urls.trim()
    if (!text || queueing) return
    setQueueing(true); setDlErr(null)
    try {
      const r = await api.queueYouTubeDownloads(text)
      if (!r.ok) setDlErr(r.error ?? 'Could not queue that.')
      else {
        setUrls('')
        if (r.warning) setDlErr(r.warning)   // partial: some lines resolved, some didn't
      }
      refreshJobs()
    } catch {
      setDlErr('Could not reach the server.')
    } finally {
      setQueueing(false)
    }
  }

  const doSearch = async () => {
    const query = q.trim()
    if (!query || searching) return
    setSearching(true); setSearchErr(null); setResults([]); setRowMsg({})
    try {
      const r = await api.searchYouTube(query, 12)
      setResults(r)
      if (r.length === 0) setSearchErr('Nothing came back — the log shows what yt-dlp said.')
    }
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

  const jobs = dl?.jobs ?? []
  const finished = jobs.filter(j => j.state === 'done' || j.state === 'failed' || j.state === 'cancelled')

  return (
    <div className="w-overlay" onMouseDown={onClose}>
      <div className="w-dialog w-raised" onMouseDown={e => e.stopPropagation()}
        style={{ width: 640, maxWidth: '94vw' }}>
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
              <fieldset className="w-group">
                <legend>Download to the library</legend>
                <div className="w-muted" style={{ marginBottom: 4 }}>
                  Paste YouTube / YouTube Music links — one per line. An album or playlist
                  link downloads all of its tracks. Files land in{' '}
                  <code>{dl?.folder ?? '…'}</code> with tags and cover art, and join the
                  library as they arrive.
                </div>
                <textarea
                  value={urls} rows={3} spellCheck={false}
                  placeholder="https://music.youtube.com/watch?v=…"
                  style={{ width: '100%', resize: 'vertical', fontFamily: 'inherit' }}
                  onChange={e => setUrls(e.target.value)}
                  onKeyDown={e => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) queueDownloads() }} />
                <div className="w-toolbar" style={{ marginTop: 4 }}>
                  <button className="w-btn w-primary" disabled={queueing || !urls.trim()}
                    onClick={queueDownloads}>
                    {queueing ? 'Queuing…' : 'Download'}
                  </button>
                  <span style={{ flex: 1 }} />
                  {finished.length > 0 && (
                    <button className="w-btn"
                      onClick={() => { api.clearYouTubeDownloads().then(refreshJobs).catch(() => {}) }}>
                      Clear finished
                    </button>
                  )}
                </div>
                {dlErr && <div className="w-err" style={{ marginTop: 4 }}>{dlErr}</div>}

                {jobs.length > 0 && (
                  <div className="w-yt-jobs">
                    {jobs.map(j => (
                      <div className="w-yt-job" key={j.id}>
                        <div className="w-yt-info">
                          <div className="w-yt-title">
                            {j.artist ? `${j.artist} — ${j.title}` : j.title}
                          </div>
                          <div className="w-yt-meta">{j.message ?? j.state}</div>
                          {(j.state === 'downloading' || j.state === 'indexing') && (
                            <div className="w-yt-bar">
                              <span style={{ width: `${Math.round(j.percent)}%` }} />
                            </div>
                          )}
                        </div>
                        <span className={
                          j.state === 'done' ? 'w-yt-pass'
                            : j.state === 'failed' ? 'w-err'
                              : 'w-muted'}>
                          {j.state === 'downloading' ? `${Math.round(j.percent)}%` : j.state}
                        </span>
                        <div className="w-yt-actions">
                          {(j.state === 'queued' || j.state === 'downloading') && (
                            <button className="w-btn"
                              onClick={() => { api.cancelYouTubeDownload(j.id).then(refreshJobs).catch(() => {}) }}>
                              Stop
                            </button>
                          )}
                          {j.state === 'done' && j.trackId != null && (
                            <button className="w-btn"
                              onClick={() => { api.addToPlaylist(j.trackId as number, 'Manual', true).catch(() => {}) }}>
                              Queue
                            </button>
                          )}
                        </div>
                      </div>
                    ))}
                  </div>
                )}
              </fieldset>

              <fieldset className="w-group">
                <legend>Search and play now</legend>
                <div className="w-muted" style={{ marginBottom: 4 }}>
                  Plays a track without filing it: it is fetched into the local web cache,
                  not the YouTube folder.
                </div>
                <div className="w-toolbar">
                  <input type="text" value={q} placeholder="Search YouTube Music…"
                    style={{ flex: 1 }} disabled={searching}
                    onChange={e => setQ(e.target.value)}
                    onKeyDown={e => { if (e.key === 'Enter') doSearch() }} />
                  <button className="w-btn" disabled={searching || !q.trim()} onClick={doSearch}>
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
              </fieldset>
            </>
          )}
        </div>
      </div>
    </div>
  )
}
