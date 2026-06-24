import { useEffect, useState } from 'react'
import * as api from './api'
import type { AnalysisInfo } from './useHub'

const STATE_NAME = ['Idle', 'Running', 'Completed', 'Failed']
const isRunning = (s: number) => s === 1

// Passive read-out for the automatic audio-analysis pass (BPM / loudness).
// There is no manual trigger: the pass runs in the background after each scan
// and at startup. This shows progress only while a pass is running (or on
// failure) and otherwise renders nothing. It refreshes the track list when a
// pass finishes so freshly-measured BPM/LUFS appear.
export default function AnalyzeBar({ live, onComplete }: { live: AnalysisInfo | null; onComplete: () => void }) {
  const [seed, setSeed] = useState<api.AnalyzeStatus | null>(null)
  const [wasRunning, setWasRunning] = useState(false)

  useEffect(() => { api.getAnalyzeStatus().then(setSeed).catch(() => {}) }, [])

  const a: AnalysisInfo | null = live ?? (seed && {
    state: seed.state, total: seed.total, processed: seed.processed,
    updated: seed.updated, failed: seed.failed, currentTitle: seed.currentTitle, message: seed.message
  })

  useEffect(() => {
    const running = a ? isRunning(a.state) : false
    if (wasRunning && !running) onComplete()
    setWasRunning(running)
  }, [a?.state]) // eslint-disable-line react-hooks/exhaustive-deps

  const running = a ? isRunning(a.state) : false
  const failed = a?.state === 3
  if (!a || (!running && !failed)) return null

  return (
    <div className="w-toolbar">
      <span className={failed ? 'w-err' : 'w-muted'}>
        {running ? 'Analysing' : (STATE_NAME[a.state] ?? '?')} — {a.processed}/{a.total}
        {a.updated > 0 || a.failed > 0 ? ` (${a.updated} measured, ${a.failed} skipped)` : ''}
      </span>
      {running && a.currentTitle && (
        <span className="w-muted" style={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {a.currentTitle}
        </span>
      )}
    </div>
  )
}
