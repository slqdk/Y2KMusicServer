import { useEffect, useState } from 'react'
import * as api from './api'
import { fmtTime } from './api'
import type { Live } from './useHub'

// Album art for the loaded track, read from the file's tags via the public
// endpoint. Falls back to a placeholder glyph when the track has no embedded
// art (the endpoint 404s in that case) or nothing is loaded.
function CoverArt({ trackId }: { trackId: number | null }) {
  const [failed, setFailed] = useState(false)
  useEffect(() => { setFailed(false) }, [trackId]) // re-attempt on track change
  const show = trackId != null && !failed
  return (
    <div className="w-cover">
      {show
        ? <img src={`/api/albumart?trackId=${trackId}`} alt="" onError={() => setFailed(true)} />
        : <span className="w-cover-ph">♪</span>}
    </div>
  )
}

// The deck visualisation (beat-clocks + VU) now lives in DeckPanel, above the
// playlist. Transport keeps the now-playing display + seek + transport buttons.
// Playback status is owned by Admin and shared with the deck panel.
export default function Transport(
  { live, status, refresh }: { live: Live; status: api.PlaybackStatus | null; refresh: () => void }
) {
  const [busy, setBusy] = useState(false)

  const act = async (fn: () => Promise<unknown>) => {
    setBusy(true)
    try { await fn() } catch { /* surfaced elsewhere */ } finally { setBusy(false); refresh() }
  }

  const playing = status?.state === 1
  const trackId = status?.trackId ?? null
  const liveA = live.progressA && live.progressA.trackId === trackId ? live.progressA : null
  const position = liveA?.positionSec ?? status?.positionSec ?? 0
  const duration = status?.durationSec ?? liveA?.durationSec ?? 0
  const np = status ?? live.nowPlaying
  const artId = status?.trackId ?? live.nowPlaying?.trackId ?? null

  const onSeek = (e: React.ChangeEvent<HTMLInputElement>) =>
    act(() => api.seek(Number(e.target.value)))

  return (
    <div className="w-now">
      {/* Now-playing display */}
      <div className="w-display">
        <CoverArt trackId={artId} />
        <div className="w-np-text">
          <div className="w-np-title">{np?.title ?? '[ No track loaded ]'}</div>
          <div className="w-np-row"><span className="w-np-label">ARTIST </span>{np?.artist ?? '---'}</div>
          <div className="w-np-row"><span className="w-np-label">ALBUM  </span>{np?.album ?? '---'}</div>
          <div className="w-np-row">
            <span className="w-np-label">STATE  </span>
            {status?.crossfading ? 'CROSSFADING' : playing ? 'PLAYING' : status?.state === 2 ? 'PAUSED' : 'STOPPED'}
          </div>
        </div>
      </div>

      {/* Seek + transport */}
      <div>
        <div className="w-seek">
          <span className="w-muted">{fmtTime(position)}</span>
          <input
            type="range" min={0} max={Math.max(1, Math.floor(duration))} value={Math.floor(position)}
            onChange={onSeek} disabled={!trackId}
          />
          <span className="w-muted">{fmtTime(duration)}</span>
        </div>
        <div className="w-transport">
          <button className="w-btn" title="Restart" disabled={!trackId || busy}
            onClick={() => act(() => api.seek(0))}>⏮</button>
          <button className="w-btn w-play w-primary" disabled={!trackId || busy}
            onClick={() => act(() => (playing ? api.pause() : api.play()))}>
            {playing ? 'Pause' : 'Play'}
          </button>
          <button className="w-btn" title="Stop" disabled={!trackId || busy}
            onClick={() => act(() => api.stop())}>■</button>
          <button className="w-btn" title="Next (crossfade now)" disabled={!trackId || busy}
            onClick={() => act(() => api.next())}>⏭</button>
        </div>
        <div className="w-seek" style={{ marginTop: 8 }}>
          <span className="w-muted">VOL</span>
          <input type="range" min={0} max={100} defaultValue={80} disabled title="Volume — settings API, Ship 4.3" />
          <span className="w-muted">4.3</span>
        </div>
      </div>
    </div>
  )
}
