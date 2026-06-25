// Typed fetch helpers for the admin API. HTTP responses are camelCase
// (ASP.NET Core web defaults). Engine state enums serialize as numbers
// (no string-enum converter is configured server-side): 0=Stopped,
// 1=Playing, 2=Paused.

export type EngineState = 0 | 1 | 2
// IsoMode crosses the wire as a number (enum), like `state`: 0 none, 1 bass,
// 2 vocal, 3 nobass (the high-pass bass-kill).
export type IsoCode = 0 | 1 | 2 | 3
export type Iso = 'none' | 'bass' | 'vocal' | 'nobass'
export const isoFromCode = (c: IsoCode | null | undefined): Iso =>
  c === 1 ? 'bass' : c === 2 ? 'vocal' : c === 3 ? 'nobass' : 'none'

export interface CategoryDto {
  id: number
  name: string
  isCustom: boolean
  enabled: boolean
  displayOrder: number
  folderCount: number
  trackCount: number
}

export interface TrackDto {
  id: number
  title: string | null
  artist: string | null
  album: string | null
  durationSec: number
  bpm: number | null
  lufs: number | null
  type: string | null
  categoryId: number | null
}

export interface TracksPage {
  total: number
  skip: number
  take: number
  items: TrackDto[]
}

// Full per-track detail for the Properties dialog: stored DB fields plus live
// file-system + audio header facts. Live fields are null when the file is
// missing or its header is unreadable.
export interface TrackProperties {
  id: number
  filePath: string
  fileName: string
  fileExists: boolean
  fileSizeBytes: number
  modifiedUtc: string | null
  title: string | null
  artist: string | null
  album: string | null
  year: number | null
  genre: string | null
  type: string | null
  categoryId: number | null
  categoryName: string | null
  durationSec: number
  bpm: number | null
  bpmConfidence: number | null
  beatPhaseOffsetSec: number | null
  lufsIntegrated: number | null
  scannedAtUtc: string | null
  audioBitrateKbps: number | null
  sampleRateHz: number | null
  channels: number | null
  codec: string | null
}

export interface PlaybackStatus {
  trackId: number | null
  title: string | null
  artist: string | null
  album: string | null
  positionSec: number
  durationSec: number
  state: EngineState
  crossfading: boolean
  nextTrackId: number | null
  nextTitle: string | null
  nextArtist: string | null
  nextStarted: boolean
  isoA: IsoCode
  isoB: IsoCode
  plannedTransition: Transition | null
  plannedReason: string | null
  armedTransition: Transition | null
}

// Every transition the engine can run (mirrors the server Transition enum names).
// The first three are crossfades (the Crossfade section); the last three are the
// musical moves (the Mixing section).
export type Transition =
  | 'NormalCrossfade' | 'BeatmatchingCrossfade' | 'BeatDropCrossfade'
  | 'VocalTease' | 'BassSwap' | 'BassBreakdown'

export interface PlaylistItem {
  id: number
  position: number
  trackId: number
  title: string | null
  artist: string | null
  durationSec: number
  bpm: number | null
  lufs: number | null
  introEndSec: number | null
  source: string
  addedBy: string | null
  addedAt: string
}

export interface AutoDjSettings {
  autoDj: boolean
  tracks: number
  bpmDev: number
}

export interface StreamStatus {
  enabled: boolean
  bitrate: number
  listeners: number
  wavListeners: number
  mp3Listeners: number
  sampleRate: number
  channels: number
}

async function req<T>(url: string, init?: RequestInit): Promise<T> {
  const r = await fetch(url, init)
  if (!r.ok) {
    let detail = ''
    try { detail = JSON.stringify(await r.json()) } catch { /* no body */ }
    throw new ApiError(r.status, `${r.status} ${r.statusText} ${detail}`.trim())
  }
  if (r.status === 204) return undefined as T
  return (await r.json()) as T
}

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message) }
}

const post = (url: string) => req<unknown>(url, { method: 'POST' })

// ── Library + categories ───────────────────────────────────────────────
export const getCategories = () => req<CategoryDto[]>('/api/admin/categories')

export const setCategoryEnabled = (id: number, on: boolean) =>
  req<{ id: number; name: string; enabled: boolean }>(
    `/api/admin/categories/${id}/enable?on=${on}`, { method: 'POST' })

export function getTracks(p: { q?: string; categoryId?: number | null; skip?: number; take?: number }) {
  const qs = new URLSearchParams()
  if (p.q) qs.set('q', p.q)
  if (p.categoryId != null) qs.set('categoryId', String(p.categoryId))
  qs.set('skip', String(p.skip ?? 0))
  qs.set('take', String(p.take ?? 100))
  return req<TracksPage>(`/api/admin/library/tracks?${qs.toString()}`)
}

// ── Track waveform + beat grid (beatgrid editor) ────────────────────────
export interface WaveformDto {
  samplesPerPoint: number
  sampleRate: number
  durationSec: number
  bpm: number | null
  phaseOffsetSec: number | null
  peaks: number[] // interleaved min,max per window, each -127..127
}
export const getWaveform = (trackId: number) =>
  req<WaveformDto>(`/api/admin/track/${trackId}/waveform`)

export const putBeatGrid = (trackId: number, body: { bpm: number; phaseOffsetSec: number }) =>
  req<{ id: number; bpm: number | null; phaseOffsetSec: number | null; mixCacheCleared: number }>(
    `/api/admin/track/${trackId}/beatgrid`,
    { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })

// Re-process one track from its file: re-reads tags + re-measures BPM/LUFS,
// then clears that track's cached mix pairs and waveform/structure caches.
export const rescanTrack = (trackId: number) =>
  post(`/api/admin/track/${trackId}/rescan`)

// Full file/tag/audio/analysis detail for the Properties dialog.
export const getTrackProperties = (trackId: number) =>
  req<TrackProperties>(`/api/admin/track/${trackId}/properties`)

// ── Playback transport ─────────────────────────────────────────────────
export const getStatus = () => req<PlaybackStatus>('/api/admin/playback/status')
export const load = (trackId: number) => post(`/api/admin/playback/load?trackId=${trackId}`)
export const play = () => post('/api/admin/playback/play')
export const pause = () => post('/api/admin/playback/pause')
export const stop = () => post('/api/admin/playback/stop')
export const seek = (seconds: number) => post(`/api/admin/playback/seek?seconds=${seconds}`)
export const next = (trackId?: number) =>
  post(`/api/admin/playback/next${trackId != null ? `?trackId=${trackId}` : ''}`)
export const queueNext = (trackId: number) => post(`/api/admin/playback/queue-next?trackId=${trackId}`)
// Manual dual-deck: cue a track onto Deck B (loads it silent at the in-point),
// start its silent preview on demand, eject it, or fire the A→B crossfade.
export const cueDeckB = (trackId: number) => post(`/api/admin/playback/cue-b?trackId=${trackId}`)
export const playDeckB = () => post('/api/admin/playback/play-b')
export const pauseDeckB = () => post('/api/admin/playback/pause-b')
export const nudgeDeckB = (ms: number) => post(`/api/admin/playback/nudge-b?ms=${ms}`)
export const ejectDeckB = () => post('/api/admin/playback/eject-b')
export const crossfadeNow = () => post('/api/admin/playback/crossfade')
// Arm a specific transition for the NEXT A→B crossfade only (the force buttons).
// Arming the same one again disarms it; it fires once, then clears back to auto.
export const armTransition = (type: Transition) =>
  post(`/api/admin/playback/arm?type=${type}`)

// Per-deck EQ isolator (DJ-mixer style — Bass = low-pass, Vocal = centre-band).
export const setIsoA = (mode: Iso) => post(`/api/admin/playback/iso-a?mode=${mode}`)
export const setIsoB = (mode: Iso) => post(`/api/admin/playback/iso-b?mode=${mode}`)

// ── Playlist + Auto DJ ─────────────────────────────────────────────────
export const getPlaylist = () => req<PlaylistItem[]>('/api/admin/playlist')
export const addToPlaylist = (
  trackId: number, source: 'Manual' | 'Auto' | 'Request' = 'Manual', atEnd = false) =>
  req<PlaylistItem[]>(
    `/api/admin/playlist/add?trackId=${trackId}&source=${source}${atEnd ? '&atEnd=true' : ''}`,
    { method: 'POST' })
export const removeEntry = (id: number) =>
  req<PlaylistItem[]>(`/api/admin/playlist/${id}`, { method: 'DELETE' })
export const clearPlaylist = () => post('/api/admin/playlist/clear')
export const getAutoDj = () => req<AutoDjSettings>('/api/admin/autodj/settings')
export function setAutoDj(p: { on?: boolean; tracks?: number; bpmDev?: number }) {
  const qs = new URLSearchParams()
  if (p.on != null) qs.set('on', String(p.on))
  if (p.tracks != null) qs.set('tracks', String(p.tracks))
  if (p.bpmDev != null) qs.set('bpmDev', String(p.bpmDev))
  return req<AutoDjSettings>(`/api/admin/autodj/settings?${qs.toString()}`, { method: 'POST' })
}
export const fillAutoDj = () => post('/api/admin/autodj/fill')

// ── Stream ──────────────────────────────────────────────────────────────
export const getStream = () => req<StreamStatus>('/api/admin/stream/status')
export const setStreamEnabled = (on: boolean) =>
  req<StreamStatus>(`/api/admin/stream/enable?on=${on}`, { method: 'POST' })
export const setStreamBitrate = (kbps: number) =>
  req<StreamStatus>(`/api/admin/stream/bitrate?kbps=${kbps}`, { method: 'POST' })

// ── Folders / slots / rename / scan ─────────────────────────────────────
export interface FolderDto { id: number; path: string }
export interface SlotDto {
  id: number
  slotIndex: number
  enabled: boolean
  timeFromHHmm: string | null
  timeToHHmm: string | null
  daysMask: number
  priority: number
}
export interface SlotInput {
  enabled: boolean
  timeFromHHmm: string | null
  timeToHHmm: string | null
  daysMask: number
  priority: number
}
export interface ScanStatus {
  state: number // 0 Idle, 1 Enumerating, 2 Scanning, 3 Completed, 4 Failed
  filesFound: number
  filesProcessed: number
  added: number
  skipped: number
  currentPath: string | null
  message: string | null
}

export const getFolders = (id: number) => req<FolderDto[]>(`/api/admin/categories/${id}/folders`)
export const addFolder = (id: number, path: string) =>
  req<{ id: number; path: string; onDiskNow: boolean }>(
    `/api/admin/categories/${id}/folders`,
    { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path }) })
export const removeFolder = (id: number, folderId: number) =>
  req<void>(`/api/admin/categories/${id}/folders/${folderId}`, { method: 'DELETE' })

// ── Filesystem browse (folder picker) ───────────────────────────────────
export interface FsEntry { name: string; path: string }
export interface FsListing {
  path: string | null      // current folder; null at the drive list
  parent: string | null    // where "up" goes; "" => drive list; null => already there
  isDriveList: boolean
  entries: FsEntry[]
}
export const browseFs = (path?: string | null) =>
  req<FsListing>(`/api/admin/fs${path ? `?path=${encodeURIComponent(path)}` : ''}`)

export const getSlots = (id: number) => req<SlotDto[]>(`/api/admin/categories/${id}/slots`)
export const putSlots = (id: number, slots: SlotInput[]) =>
  req<SlotDto[]>(`/api/admin/categories/${id}/slots`,
    { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(slots) })

export const renameCategory = (id: number, name: string) =>
  req<{ id: number; name: string; isCustom: boolean }>(`/api/admin/categories/${id}/rename`,
    { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name }) })

// Clears a category's scanned tracks and their derived data (mix/structure
// caches, playlist entries, requests). Keeps the folder paths and schedule.
export const clearCategoryData = (id: number) =>
  req<{ removed: number }>(`/api/admin/categories/${id}/clear-data`, { method: 'POST' })

export const startScan = (categoryId?: number) =>
  req<ScanStatus>(`/api/admin/scan${categoryId != null ? `?categoryId=${categoryId}` : ''}`, { method: 'POST' })
export const getScanStatus = () => req<ScanStatus>('/api/admin/scan/status')

export interface AnalyzeStatus {
  state: number // 0 Idle, 1 Running, 2 Completed, 3 Failed
  total: number
  processed: number
  updated: number
  failed: number
  currentTitle: string | null
  message: string | null
}
export const startAnalyze = (all = false) =>
  req<AnalyzeStatus>(`/api/admin/analyze?all=${all}`, { method: 'POST' })
export const getAnalyzeStatus = () => req<AnalyzeStatus>('/api/admin/analyze/status')

// ── General settings ────────────────────────────────────────────────────
export interface SettingsDto {
  nextTriggerPct: number
  nextFadeSeconds: number
  autoDj: boolean
  autoDjTracks: number
  autoDjBpmDev: number
  scanWorkers: number
  normalizeEnabled: boolean
  limiterEnabled: boolean
  targetLufs: number
  volume: number
  streamingEnabled: boolean
  streamingBitrate: number
  allowWebNext: boolean
  showWebCategories: boolean
  debugLogging: boolean
}
export interface SettingsUpdate {
  nextTriggerPct?: number
  nextFadeSeconds?: number
  normalizeEnabled?: boolean
  limiterEnabled?: boolean
  targetLufs?: number
  volume?: number
  scanWorkers?: number
  allowWebNext?: boolean
  showWebCategories?: boolean
  debugLogging?: boolean
}
export const getSettings = () => req<SettingsDto>('/api/admin/settings')
export const putSettings = (u: SettingsUpdate) =>
  req<SettingsDto>('/api/admin/settings',
    { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(u) })

// ── Auto-mix rules (mixrules.json — a separate store; applies immediately) ──
export interface MixRulesDto {
  crossfadeAuto: boolean
  mixingAuto: boolean
  bpmTolerance: number
  vocalTease: boolean
  bassSwap: boolean
  bassBreakdown: boolean
  deckBEntryLevel: number
  bassHoldBars: number
  maxOverlapBars: number
  sameTempoBars: number
  relatedTempoBars: number
}
export const getMixRules = () => req<MixRulesDto>('/api/admin/mix/rules')
// PUT replaces the whole object (the server clamps and returns what it stored).
export const putMixRules = (r: MixRulesDto) =>
  req<MixRulesDto>('/api/admin/mix/rules',
    { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(r) })

// ── Listener requests (admin side) ──────────────────────────────────────
export interface RequestDto {
  id: number
  trackId: number
  title: string | null
  artist: string | null
  requesterName: string | null
  status: string
  createdAt: string
}
export const getRequests = () => req<RequestDto[]>('/api/admin/requests')
export const acceptRequest = (id: number) => req<unknown>(`/api/admin/requests/${id}/accept`, { method: 'POST' })
export const dismissRequest = (id: number) => req<unknown>(`/api/admin/requests/${id}/dismiss`, { method: 'POST' })

// ── Activity log (Ship 1 backend) ───────────────────────────────────────
// The ring buffer behind GET /api/admin/logs; the live feed is the hub's
// `logEntry` event. `debugEnabled` mirrors Settings.DebugLogging (verbose).
export interface LogEntry {
  seq: number
  timestampUtc: string
  level: string   // Verbose | Debug | Information | Warning | Error | Fatal
  source: string
  message: string
  exception: string | null
}
export interface LogsSnapshot {
  level: string
  debugEnabled: boolean
  capacity: number
  entries: LogEntry[]
}
export function getLogs(p?: { take?: number; level?: string }) {
  const qs = new URLSearchParams()
  if (p?.take != null) qs.set('take', String(p.take))
  if (p?.level) qs.set('level', p.level)
  const q = qs.toString()
  return req<LogsSnapshot>(`/api/admin/logs${q ? `?${q}` : ''}`)
}

// ── Small formatters ────────────────────────────────────────────────────
export function fmtTime(sec: number | null | undefined): string {
  if (sec == null || !isFinite(sec) || sec < 0) return '--:--'
  const s = Math.floor(sec)
  const m = Math.floor(s / 60)
  return `${m}:${String(s % 60).padStart(2, '0')}`
}


// ── Network shares ─────────────────────────────────────────────────────
export interface NetworkShare { host: string; username: string }

export const getNetworkShares = () =>
  req<NetworkShare[]>('/api/admin/network')

// Connect reports failure in the body (not only via HTTP status), so read the
// JSON either way and surface the server's plain-English error.
export async function connectNetworkShare(body: { path: string; username: string; password: string }):
  Promise<{ ok: boolean; host?: string; message?: string; error?: string }> {
  const r = await fetch('/api/admin/network/connect', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  const data = (await r.json().catch(() => ({}))) as { host?: string; message?: string; error?: string }
  return { ok: r.ok, ...data }
}

export const forgetNetworkShare = (host: string) =>
  req<{ removed: boolean; host: string }>(
    `/api/admin/network/${encodeURIComponent(host)}`, { method: 'DELETE' })
