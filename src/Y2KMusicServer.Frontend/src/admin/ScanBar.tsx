import { useEffect, useState } from 'react'
import * as api from './api'
import type { ScanInfo } from './useHub'

const STATE_NAME = ['Idle', 'Enumerating', 'Scanning', 'Completed', 'Failed']
const isRunning = (s: number) => s === 1 || s === 2

// Passive read-out for the automatic library scan. There is no manual trigger:
// scans fire on folder-add and at startup. This shows progress only while a
// scan is running (or on failure) and otherwise renders nothing, so it never
// leaves an idle status line behind. It still refreshes the category/track
// lists when a scan finishes so newly-indexed tracks appear.
export default function ScanBar({ live, onComplete }: { live: ScanInfo | null; onComplete: () => void }) {
  const [seed, setSeed] = useState<api.ScanStatus | null>(null)
  const [wasRunning, setWasRunning] = useState(false)

  // Seed from the HTTP snapshot once (covers "no hub event yet").
  useEffect(() => { api.getScanStatus().then(setSeed).catch(() => {}) }, [])

  // Live hub events win once they arrive; fall back to the seed snapshot.
  const scan: ScanInfo | null = live ?? (seed && {
    state: seed.state, filesFound: seed.filesFound, filesProcessed: seed.filesProcessed,
    added: seed.added, skipped: seed.skipped, queued: seed.queued,
    currentPath: seed.currentPath, message: seed.message
  })

  // Refresh categories/library when a scan finishes.
  useEffect(() => {
    const running = scan ? isRunning(scan.state) : false
    if (wasRunning && !running) onComplete()
    setWasRunning(running)
  }, [scan?.state]) // eslint-disable-line react-hooks/exhaustive-deps

  const running = scan ? isRunning(scan.state) : false
  const failed = scan?.state === 4
  if (!scan || (!running && !failed)) return null

  return (
    <div className="w-toolbar">
      <span className={failed ? 'w-err' : 'w-muted'}>
        {STATE_NAME[scan.state] ?? '?'} — {scan.filesProcessed}/{scan.filesFound} files
        {scan.added > 0 || scan.skipped > 0 ? ` (+${scan.added} new, ${scan.skipped} skipped)` : ''}
        {scan.queued > 0 ? ` · ${scan.queued} queued` : ''}
      </span>
      {running && scan.currentPath && (
        <span className="w-muted" style={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {scan.currentPath}
        </span>
      )}
    </div>
  )
}
