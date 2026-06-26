import { useEffect, useRef, useState } from 'react'
import * as signalR from '@microsoft/signalr'
import type { EngineState, LogEntry } from './api'

// The SignalR JSON wire casing for our PascalCase records isn't guaranteed to
// match the HTTP camelCase, so every payload is read tolerantly (camelCase
// first, then PascalCase). Enums arrive as numbers.

export type ConnState = 'connecting' | 'connected' | 'reconnecting' | 'failed'

export interface NowPlaying {
  trackId: number | null
  title: string | null
  artist: string | null
  album: string | null
  durationSec: number
  state: EngineState
}
export interface DeckProgress {
  deck: string
  trackId: number | null
  positionSec: number
  durationSec: number
  inPointSec: number
  bpm: number | null
  phaseOffsetSec: number | null
}
export interface Vu { left: number; right: number }
// A confirmed kick onset, stamped on arrival so the beat strip can scroll it
// left over time. `strength` is the FftAnalyser BassOnset (0.1–1.0).
export interface BeatTick { t: number; strength: number }
export interface TransitionInfo {
  fromTrackId: number | null
  toTrackId: number | null
  fadeSeconds: number
  smartMix: boolean
  fadeShortened: boolean
  smartBeatState: string | null
  reason: string | null
}
export interface ScanInfo {
  state: number // 0 Idle, 1 Enumerating, 2 Scanning, 3 Completed, 4 Failed
  filesFound: number
  filesProcessed: number
  added: number
  skipped: number
  queued: number
  currentPath: string | null
  message: string | null
}

export interface AnalysisInfo {
  state: number // 0 Idle, 1 Running, 2 Completed, 3 Failed
  total: number
  processed: number
  updated: number
  failed: number
  currentTitle: string | null
  message: string | null
}

export interface Live {
  conn: ConnState
  nowPlaying: NowPlaying | null
  progressA: DeckProgress | null
  progressB: DeckProgress | null
  vuA: Vu
  vuB: Vu
  beatSeqA: number
  beatSeqB: number
  beatsA: BeatTick[]
  beatsB: BeatTick[]
  transitions: TransitionInfo[]
  scan: ScanInfo | null
  analysis: AnalysisInfo | null
  logs: LogEntry[]
}

// camelCase-first, PascalCase-fallback field read.
function f<T>(o: any, name: string): T | undefined {
  if (o == null) return undefined
  const camel = name[0].toLowerCase() + name.slice(1)
  const pascal = name[0].toUpperCase() + name.slice(1)
  return (o[camel] ?? o[pascal]) as T | undefined
}
const num = (o: any, n: string, d = 0) => { const v = f<number>(o, n); return typeof v === 'number' ? v : d }
const str = (o: any, n: string) => { const v = f<string>(o, n); return v == null ? null : String(v) }
const idOf = (o: any, n: string) => { const v = f<number>(o, n); return typeof v === 'number' ? v : null }

function emptyLive(): Live {
  return {
    conn: 'connecting',
    nowPlaying: null,
    progressA: null, progressB: null,
    vuA: { left: 0, right: 0 }, vuB: { left: 0, right: 0 },
    beatSeqA: 0, beatSeqB: 0,
    beatsA: [], beatsB: [],
    transitions: [],
    scan: null,
    analysis: null,
    logs: []
  }
}

export function useHub(): Live {
  const [live, setLive] = useState<Live>(emptyLive)
  const ref = useRef<Live>(live)
  const dirty = useRef(false)

  useEffect(() => {
    const mark = (mut: (l: Live) => void) => { mut(ref.current); dirty.current = true }

    const conn = new signalR.HubConnectionBuilder()
      .withUrl('/hub/playback')
      .withAutomaticReconnect()
      .build()

    const setConn = (c: ConnState) => mark(l => { l.conn = c })

    conn.onreconnecting(() => setConn('reconnecting'))
    conn.onreconnected(() => setConn('connected'))
    conn.onclose(() => setConn('failed'))

    conn.on('nowPlaying', (m: any) => mark(l => {
      l.nowPlaying = {
        trackId: idOf(m, 'trackId'),
        title: str(m, 'title'),
        artist: str(m, 'artist'),
        album: str(m, 'album'),
        durationSec: num(m, 'durationSec'),
        state: (num(m, 'state') as EngineState)
      }
    }))

    conn.on('deckProgress', (m: any) => mark(l => {
      const p: DeckProgress = {
        deck: str(m, 'deck') ?? 'A',
        trackId: idOf(m, 'trackId'),
        positionSec: num(m, 'positionSec'),
        durationSec: num(m, 'durationSec'),
        inPointSec: num(m, 'inPointSec'),
        bpm: f<number>(m, 'bpm') ?? null,
        phaseOffsetSec: f<number>(m, 'phaseOffsetSec') ?? null
      }
      if (p.deck === 'B') l.progressB = p; else l.progressA = p
    }))

    conn.on('vu', (m: any) => mark(l => {
      const deck = str(m, 'deck') ?? 'A'
      const v: Vu = { left: num(m, 'left'), right: num(m, 'right') }
      if (deck === 'B') l.vuB = v; else l.vuA = v
    }))

    conn.on('beat', (m: any) => mark(l => {
      const deck = str(m, 'deck') ?? 'A'
      const tick: BeatTick = { t: performance.now(), strength: num(m, 'strength', 1) }
      if (deck === 'B') {
        l.beatSeqB++
        l.beatsB = [...l.beatsB, tick].slice(-96)
      } else {
        l.beatSeqA++
        l.beatsA = [...l.beatsA, tick].slice(-96)
      }
    }))

    conn.on('transition', (m: any) => mark(l => {
      const t: TransitionInfo = {
        fromTrackId: idOf(m, 'fromTrackId'),
        toTrackId: idOf(m, 'toTrackId'),
        fadeSeconds: num(m, 'fadeSeconds'),
        smartMix: !!f<boolean>(m, 'smartMix'),
        fadeShortened: !!f<boolean>(m, 'fadeShortened'),
        smartBeatState: str(m, 'smartBeatState'),
        reason: str(m, 'reason')
      }
      l.transitions = [t, ...l.transitions].slice(0, 8)
    }))

    const onScan = (m: any) => mark(l => {
      l.scan = {
        state: num(m, 'state'),
        filesFound: num(m, 'filesFound'),
        filesProcessed: num(m, 'filesProcessed'),
        added: num(m, 'added'),
        skipped: num(m, 'skipped'),
        queued: num(m, 'queued'),
        currentPath: str(m, 'currentPath'),
        message: str(m, 'message')
      }
    })
    conn.on('scanProgress', onScan)
    conn.on('scanComplete', onScan)

    const onAnalyze = (m: any) => mark(l => {
      l.analysis = {
        state: num(m, 'state'),
        total: num(m, 'total'),
        processed: num(m, 'processed'),
        updated: num(m, 'updated'),
        failed: num(m, 'failed'),
        currentTitle: str(m, 'currentTitle'),
        message: str(m, 'message')
      }
    })
    conn.on('analyzeProgress', onAnalyze)
    conn.on('analyzeComplete', onAnalyze)

    // Live activity-log feed (Ship 1 backend pushes one logEntry per line).
    // Kept as a capped rolling buffer; the LogPanel merges this with the
    // initial HTTP snapshot (de-duplicated by seq).
    conn.on('logEntry', (m: any) => mark(l => {
      const e: LogEntry = {
        seq: num(m, 'seq'),
        timestampUtc: str(m, 'timestampUtc') ?? '',
        level: str(m, 'level') ?? 'Information',
        source: str(m, 'source') ?? '',
        message: str(m, 'message') ?? '',
        exception: str(m, 'exception')
      }
      l.logs = [...l.logs, e]
      if (l.logs.length > 1000) l.logs = l.logs.slice(-1000)
    }))

    conn.start()
      .then(() => setConn('connected'))
      .catch(() => setConn('failed'))

    let raf = 0
    const flush = () => {
      if (dirty.current) { dirty.current = false; setLive({ ...ref.current }) }
      raf = requestAnimationFrame(flush)
    }
    raf = requestAnimationFrame(flush)

    return () => { cancelAnimationFrame(raf); void conn.stop() }
  }, [])

  return live
}
