import { useEffect, useMemo, useRef, useState } from 'react'
import * as api from './api'
import type { LogEntry } from './api'
import type { Live } from './useHub'

// Serilog level ordering, for the "show this level and above" filter.
const RANK: Record<string, number> = {
  Verbose: 0, Debug: 1, Information: 2, Warning: 3, Error: 4, Fatal: 5
}
const FILTER_RANK: Record<string, number> = {
  All: 0, Debug: 1, Information: 2, Warning: 3, Error: 4
}
const SHORT: Record<string, string> = {
  Verbose: 'VRB', Debug: 'DBG', Information: 'INFO', Warning: 'WARN', Error: 'ERR', Fatal: 'FATAL'
}
const rankOf = (lvl: string) => RANK[lvl] ?? 2
const shortLvl = (lvl: string) => SHORT[lvl] ?? lvl.slice(0, 4).toUpperCase()

function clock(iso: string): string {
  const d = new Date(iso)
  if (isNaN(d.getTime())) return '--:--:--'
  const p = (n: number) => String(n).padStart(2, '0')
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`
}

export default function LogPanel({ live, onClose }: { live: Live; onClose: () => void }) {
  const [bySeq, setBySeq] = useState<Map<number, LogEntry>>(() => new Map())
  const [hiddenBeforeSeq, setHiddenBeforeSeq] = useState(0)
  const [minLevel, setMinLevel] = useState('All')
  const [followTail, setFollowTail] = useState(true)
  const [verbose, setVerbose] = useState<boolean | null>(null)
  const [verboseBusy, setVerboseBusy] = useState(false)
  const [loadErr, setLoadErr] = useState<string | null>(null)
  const [copyMsg, setCopyMsg] = useState<string | null>(null)
  const [hiddenSources, setHiddenSources] = useState<Set<string>>(() => new Set())

  const viewRef = useRef<HTMLDivElement | null>(null)

  // Seed history + current verbose state from the HTTP snapshot once.
  useEffect(() => {
    let alive = true
    api.getLogs({ take: 1000 })
      .then(snap => {
        if (!alive) return
        setBySeq(prev => {
          const m = new Map(prev)
          for (const e of snap.entries) m.set(e.seq, e)
          return m
        })
        setVerbose(snap.debugEnabled)
      })
      .catch(() => { if (alive) setLoadErr('Could not load log history.') })
    return () => { alive = false }
  }, [])

  // Merge the live feed in as it arrives (de-duplicated by seq, capped).
  useEffect(() => {
    if (live.logs.length === 0) return
    setBySeq(prev => {
      const m = new Map(prev)
      for (const e of live.logs) m.set(e.seq, e)
      if (m.size > 1500) {
        const keep = [...m.values()].sort((a, b) => a.seq - b.seq).slice(-1500)
        return new Map(keep.map(e => [e.seq, e] as const))
      }
      return m
    })
  }, [live.logs])

  const allSources = useMemo(
    () => [...new Set([...bySeq.values()].map(e => e.source).filter(Boolean))].sort(),
    [bySeq]
  )

  const entries = useMemo(() => {
    const minRank = FILTER_RANK[minLevel] ?? 0
    return [...bySeq.values()]
      .filter(e => e.seq > hiddenBeforeSeq && rankOf(e.level) >= minRank && !hiddenSources.has(e.source))
      .sort((a, b) => a.seq - b.seq)
  }, [bySeq, hiddenBeforeSeq, minLevel, hiddenSources])

  // Auto-scroll to the newest line while following the tail.
  useEffect(() => {
    if (!followTail) return
    const el = viewRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [entries, followTail])

  const onScroll = () => {
    const el = viewRef.current
    if (!el) return
    const nearBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 8
    setFollowTail(nearBottom)
  }

  const toggleVerbose = async (on: boolean) => {
    setVerboseBusy(true); setLoadErr(null)
    try {
      const r = await api.putSettings({ debugLogging: on })
      setVerbose(r.debugLogging)
    } catch {
      setLoadErr('Could not change verbose logging.')
    } finally {
      setVerboseBusy(false)
    }
  }

  const clearView = () => {
    let max = hiddenBeforeSeq
    for (const e of bySeq.values()) if (e.seq > max) max = e.seq
    setHiddenBeforeSeq(max)
  }

  const toggleSource = (s: string) =>
    setHiddenSources(prev => {
      const next = new Set(prev)
      if (next.has(s)) next.delete(s)
      else next.add(s)
      return next
    })

  const copyVisible = async () => {
    const text = entries.map(e => {
      const base = `[${clock(e.timestampUtc)}] ${e.level} ${e.source ? e.source + ': ' : ''}${e.message}`
      return e.exception ? `${base}\n${e.exception}` : base
    }).join('\n')
    try {
      await navigator.clipboard.writeText(text)
      setCopyMsg(`Copied ${entries.length} line${entries.length === 1 ? '' : 's'}.`)
    } catch {
      setCopyMsg('Copy failed (clipboard blocked).')
    }
    setTimeout(() => setCopyMsg(null), 2500)
  }

  return (
    <div className="w-panel w-raised" style={{ margin: 4 }}>
      <div className="w-panelhead">
        Activity Log
        <button className="w-btn" onClick={onClose}
          style={{ float: 'right', minHeight: 16, padding: '0 7px', marginTop: -1 }}>✕</button>
      </div>

      <div className="w-toolbar">
        <label className="w-check" title="Writes Settings.DebugLogging — the same switch as Settings → Debug logging">
          <input type="checkbox" checked={!!verbose} disabled={verbose === null || verboseBusy}
            onChange={e => toggleVerbose(e.target.checked)} />
          Verbose logging
        </label>

        <label className="w-check">
          Level:
          <select value={minLevel} onChange={e => setMinLevel(e.target.value)}>
            <option>All</option>
            <option>Debug</option>
            <option>Information</option>
            <option>Warning</option>
            <option>Error</option>
          </select>
        </label>

        <label className="w-check">
          <input type="checkbox" checked={followTail} onChange={e => setFollowTail(e.target.checked)} />
          Follow tail
        </label>

        {allSources.length > 0 && (
          <span className="w-check" style={{ gap: 4, flexWrap: 'wrap' }}>
            Sources:
            {allSources.map(s => {
              const on = !hiddenSources.has(s)
              return (
                <button key={s} className="w-btn" onClick={() => toggleSource(s)}
                  title={on ? `Hide ${s} lines` : `Show ${s} lines`}
                  style={{ minHeight: 16, padding: '0 6px', opacity: on ? 1 : 0.4, textDecoration: on ? 'none' : 'line-through' }}>
                  {s}
                </button>
              )
            })}
          </span>
        )}

        <button className="w-btn" onClick={clearView}>Clear view</button>
        <button className="w-btn" onClick={copyVisible} disabled={entries.length === 0}>Copy</button>

        <span className="w-spacer" />
        <span className="w-muted">
          {entries.length} lines · {verbose == null ? '…' : verbose ? 'verbose (Debug)' : 'normal (Information)'}
        </span>
        {live.conn !== 'connected' && <span className="w-logpaused">SignalR {live.conn} — live paused</span>}
        {copyMsg && <span className="w-muted">{copyMsg}</span>}
        {loadErr && <span className="w-err">{loadErr}</span>}
      </div>

      <div className="w-logview" ref={viewRef} onScroll={onScroll}>
        {entries.length === 0 && (
          <div className="w-logempty">No log entries{minLevel !== 'All' ? ` at ${minLevel}+ level` : ''}.</div>
        )}
        {entries.map(e => (
          <div key={e.seq} className={`w-logline w-log-${e.level.toLowerCase()}`}>
            <span className="w-log-time">[{clock(e.timestampUtc)}]</span>{' '}
            <span className="w-log-lvl">{shortLvl(e.level)}</span>{' '}
            {e.source && <span className="w-log-src">{e.source}: </span>}
            <span className="w-log-msg" style={{ whiteSpace: 'pre-wrap' }}>{e.message}</span>
            {e.exception && <div className="w-log-exc">{e.exception}</div>}
          </div>
        ))}
      </div>

      <div className="w-muted" style={{ margin: '4px 2px 0' }}>
        Live feed over SignalR; history from the server's in-memory buffer. “Clear view” only hides
        current lines here — the server log and the daily file are untouched.
      </div>
    </div>
  )
}
