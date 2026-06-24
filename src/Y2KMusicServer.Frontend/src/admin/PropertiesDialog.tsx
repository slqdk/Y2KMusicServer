import { useEffect, useState } from 'react'
import * as api from './api'
import { fmtTime } from './api'

// Human-readable byte size (KB/MB/GB, binary).
function fmtBytes(n: number): string {
  if (!Number.isFinite(n) || n <= 0) return '0 bytes'
  const u = ['bytes', 'KB', 'MB', 'GB', 'TB']
  let i = 0, v = n
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++ }
  const s = i === 0 ? String(v) : v.toFixed(v < 10 ? 2 : v < 100 ? 1 : 0)
  return `${s} ${u[i]}`
}

// Local date+time, or em dash for null.
function fmtDate(iso: string | null): string {
  if (!iso) return '—'
  const d = new Date(iso)
  return isNaN(d.getTime()) ? '—' : d.toLocaleString()
}

function chans(n: number | null): string {
  if (n == null) return '—'
  if (n === 1) return 'Mono'
  if (n === 2) return 'Stereo'
  return `${n} channels`
}

// Strip the file name to show just the containing folder (handles \ and /).
function folderOf(path: string): string {
  const m = path.replace(/[\\/][^\\/]*$/, '')
  return m === path ? '—' : m
}

export default function PropertiesDialog({ trackId, onClose }:
  { trackId: number; onClose: () => void }) {

  const [p, setP] = useState<api.TrackProperties | null>(null)
  const [err, setErr] = useState<string | null>(null)

  useEffect(() => {
    let live = true
    setP(null); setErr(null)
    api.getTrackProperties(trackId)
      .then(d => { if (live) setP(d) })
      .catch(() => { if (live) setErr('Could not read properties for this track.') })
    return () => { live = false }
  }, [trackId])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div className="w-overlay" onMouseDown={onClose}>
      <div className="w-dialog w-raised" onMouseDown={e => e.stopPropagation()}
        style={{ width: 460, maxWidth: '92vw' }}>
        <div className="w-titlebar">
          <span className="w-app">Properties{p ? ` — ${p.fileName}` : ''}</span>
          <span style={{ flex: 1 }} />
          <button className="w-btn" onClick={onClose} style={{ minHeight: 16, padding: '0 7px' }}>✕</button>
        </div>

        <div className="w-dialog-body">
          {err && <div className="w-err">{err}</div>}
          {!p && !err && <div className="w-muted" style={{ padding: 4 }}>Reading file…</div>}

          {p && (
            <>
              <fieldset className="w-group">
                <legend>File</legend>
                <dl className="w-props">
                  <dt>Name</dt><dd>{p.fileName}</dd>
                  <dt>Folder</dt><dd>{folderOf(p.filePath)}</dd>
                  <dt>Type</dt><dd>{p.type ?? '—'}</dd>
                  <dt>Size</dt><dd>{p.fileExists ? fmtBytes(p.fileSizeBytes) : 'file not found on disk'}</dd>
                  <dt>Modified</dt><dd>{fmtDate(p.modifiedUtc)}</dd>
                </dl>
              </fieldset>

              <fieldset className="w-group">
                <legend>Tags</legend>
                <dl className="w-props">
                  <dt>Title</dt><dd>{p.title ?? '—'}</dd>
                  <dt>Artist</dt><dd>{p.artist ?? '—'}</dd>
                  <dt>Album</dt><dd>{p.album ?? '—'}</dd>
                  <dt>Year</dt><dd>{p.year ?? '—'}</dd>
                  <dt>Genre</dt><dd>{p.genre ?? '—'}</dd>
                </dl>
              </fieldset>

              <fieldset className="w-group">
                <legend>Audio</legend>
                <dl className="w-props">
                  <dt>Duration</dt><dd>{fmtTime(p.durationSec)}</dd>
                  <dt>Bitrate</dt><dd>{p.audioBitrateKbps != null ? `${p.audioBitrateKbps} kbps` : '—'}</dd>
                  <dt>Sample rate</dt><dd>{p.sampleRateHz != null ? `${p.sampleRateHz.toLocaleString()} Hz` : '—'}</dd>
                  <dt>Channels</dt><dd>{chans(p.channels)}</dd>
                  <dt>Codec</dt><dd>{p.codec ?? '—'}</dd>
                </dl>
              </fieldset>

              <fieldset className="w-group">
                <legend>Analysis</legend>
                <dl className="w-props">
                  <dt>BPM</dt>
                  <dd>{p.bpm != null
                    ? `${p.bpm.toFixed(1)}${p.bpmConfidence != null ? ` (confidence ${(p.bpmConfidence * 100).toFixed(0)}%)` : ''}`
                    : '—'}</dd>
                  <dt>Beat phase</dt><dd>{p.beatPhaseOffsetSec != null ? `${p.beatPhaseOffsetSec.toFixed(3)} s` : '—'}</dd>
                  <dt>Loudness</dt><dd>{p.lufsIntegrated != null ? `${p.lufsIntegrated.toFixed(1)} LUFS` : '—'}</dd>
                </dl>
              </fieldset>

              <fieldset className="w-group">
                <legend>Library</legend>
                <dl className="w-props">
                  <dt>Category</dt><dd>{p.categoryName ?? (p.categoryId != null ? `#${p.categoryId}` : 'unassigned')}</dd>
                  <dt>Track ID</dt><dd>{p.id}</dd>
                  <dt>Scanned</dt><dd>{fmtDate(p.scannedAtUtc)}</dd>
                </dl>
              </fieldset>
            </>
          )}
        </div>
      </div>
    </div>
  )
}
