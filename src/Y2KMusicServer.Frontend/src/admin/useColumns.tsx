import { useCallback, useEffect, useRef, useState, type MouseEvent as ReactMouseEvent } from 'react'

// Minimum on-screen width (px) a column may be dragged down to.
const MIN_PX = 36

/**
 * Fixed-layout resizable columns that always sum to the container width, so the
 * table never scrolls horizontally. Widths are kept as unitless ratios and
 * rendered as percentages; dragging a divider moves width between the two
 * adjacent columns only, keeping the total constant. Widths persist per table
 * in localStorage, keyed by `storageKey`.
 *
 * Usage: spread {colgroup} as the table's first child, give the table the
 * `w-grid` class, and drop a <ColResizer onMouseDown={startResize(i)} /> into
 * the header cell of every column except the last.
 */
export function useColumnWidths(storageKey: string, defaults: number[]) {
  const [widths, setWidths] = useState<number[]>(() => {
    try {
      const raw = localStorage.getItem(storageKey)
      if (raw) {
        const arr = JSON.parse(raw)
        if (Array.isArray(arr) && arr.length === defaults.length
            && arr.every((n: unknown) => typeof n === 'number' && n > 0))
          return arr as number[]
      }
    } catch { /* ignore corrupt/absent */ }
    return defaults
  })

  useEffect(() => {
    try { localStorage.setItem(storageKey, JSON.stringify(widths)) } catch { /* ignore */ }
  }, [storageKey, widths])

  const drag = useRef<{ i: number; startX: number; base: number[]; tablePx: number } | null>(null)

  const onMove = useCallback((e: MouseEvent) => {
    const d = drag.current
    if (!d) return
    const sum = d.base.reduce((a, b) => a + b, 0)
    const ratioPerPx = sum / Math.max(1, d.tablePx)
    const minRatio = MIN_PX * ratioPerPx
    const left = d.base[d.i], right = d.base[d.i + 1]
    let delta = (e.clientX - d.startX) * ratioPerPx
    // Clamp so neither adjacent column shrinks below the minimum.
    delta = Math.max(-(left - minRatio), Math.min(right - minRatio, delta))
    const next = d.base.slice()
    next[d.i] = left + delta
    next[d.i + 1] = right - delta
    setWidths(next)
  }, [])

  const onUp = useCallback(() => {
    drag.current = null
    window.removeEventListener('mousemove', onMove)
    window.removeEventListener('mouseup', onUp)
    document.body.style.cursor = ''
    document.body.style.userSelect = ''
  }, [onMove])

  const startResize = useCallback((i: number) => (e: ReactMouseEvent) => {
    e.preventDefault()
    e.stopPropagation()
    const th = (e.currentTarget as HTMLElement).closest('th')
    const table = th?.closest('table') as HTMLElement | null
    drag.current = { i, startX: e.clientX, base: widths.slice(), tablePx: table?.clientWidth ?? 800 }
    window.addEventListener('mousemove', onMove)
    window.addEventListener('mouseup', onUp)
    document.body.style.cursor = 'col-resize'
    document.body.style.userSelect = 'none'
  }, [widths, onMove, onUp])

  const sum = widths.reduce((a, b) => a + b, 0)
  const colgroup = (
    <colgroup>
      {widths.map((w, i) => <col key={i} style={{ width: `${(w / sum) * 100}%` }} />)}
    </colgroup>
  )

  return { widths, colgroup, startResize }
}

/** Drag handle for a column divider; sits at the right edge of a header cell. */
export function ColResizer({ onMouseDown }: { onMouseDown: (e: ReactMouseEvent) => void }) {
  return (
    <span className="w-colresizer"
      onMouseDown={onMouseDown}
      onClick={e => e.stopPropagation()}
      onDoubleClick={e => e.stopPropagation()} />
  )
}
