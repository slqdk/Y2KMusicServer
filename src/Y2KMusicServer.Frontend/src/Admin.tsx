import { useCallback, useEffect, useState } from 'react'
import './admin/theme.css'
import * as api from './admin/api'
import { useHub } from './admin/useHub'
import SettingsDialog from './admin/SettingsDialog'
import LibraryBrowser from './admin/LibraryBrowser'
import PlaylistPanel from './admin/PlaylistPanel'
import DeckPanel from './admin/DeckPanel'
import LogPanel from './admin/LogPanel'

const THEMES: [string, string][] = [
  ['win2k', 'Windows 2000'],
  ['winxp', 'Windows XP'],
  ['win7', 'Windows 7'],
  ['win10', 'Windows 10'],
  ['win11', 'Windows 11'],
]

function readTheme(): string {
  try { return localStorage.getItem('y2k-admin-theme') || 'win2k' } catch { return 'win2k' }
}

export default function Admin() {
  const live = useHub()
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [logOpen, setLogOpen] = useState(false)
  const [theme, setTheme] = useState<string>(readTheme)
  const [status, setStatus] = useState<api.PlaybackStatus | null>(null)
  // Mixing modes shown as toggle buttons on the deck panel. Smart Mix / SmartBeat
  // live in Settings; auto-mix Enabled lives in the (separate) mix-rules store.
  const [settings, setSettings] = useState<api.SettingsDto | null>(null)
  const [mixRules, setMixRules] = useState<api.MixRulesDto | null>(null)

  useEffect(() => { try { localStorage.setItem('y2k-admin-theme', theme) } catch { /* ignore */ } }, [theme])

  // Playback status is polled here and shared by the deck panel + transport, so
  // a manual action in either reflects in both right away.
  const refreshStatus = useCallback(() => { api.getStatus().then(setStatus).catch(() => {}) }, [])
  useEffect(() => {
    refreshStatus()
    const id = setInterval(refreshStatus, 1000)
    return () => clearInterval(id)
  }, [refreshStatus])

  // Load the mixing-mode values (and refresh after the Settings dialog closes,
  // since it edits the same Settings + mix-rules stores).
  const refreshModes = useCallback(() => {
    api.getSettings().then(setSettings).catch(() => {})
    api.getMixRules().then(setMixRules).catch(() => {})
  }, [])
  useEffect(() => { refreshModes() }, [refreshModes])

  // Flip one mixing mode and reflect the server's stored value. Smart Mix /
  // SmartBeat are partial Settings PUTs; auto-mix re-PUTs the whole rules object.
  const toggleMode = useCallback(async (mode: 'smartMix' | 'smartBeatFader' | 'autoMix') => {
    try {
      if (mode === 'autoMix') {
        if (!mixRules) return
        setMixRules(await api.putMixRules({ ...mixRules, enabled: !mixRules.enabled }))
      } else if (settings) {
        const upd = mode === 'smartMix'
          ? { smartMix: !settings.smartMix }
          : { smartBeatFader: !settings.smartBeatFader }
        setSettings(await api.putSettings(upd))
      }
    } catch { /* surfaced by the toggle not changing */ }
  }, [settings, mixRules])

  const mixModes = settings && mixRules
    ? { smartMix: settings.smartMix, smartBeatFader: settings.smartBeatFader, autoMix: mixRules.enabled }
    : null

  // "Play now" from a library row: if a track is on air and playing, cue the
  // chosen track onto Deck B and crossfade to it immediately; if nothing is
  // playing there's nothing to mix from, so load it on Deck A and start.
  // Status is read fresh so the choice matches the engine, not a stale poll.
  const playNow = useCallback(async (trackId: number) => {
    let s: api.PlaybackStatus | null = null
    try { s = await api.getStatus() } catch { /* fall back to load + play */ }
    const onAir = s != null && s.state === 1 && s.trackId != null && !s.crossfading
    try {
      if (onAir) {
        await api.cueDeckB(trackId)
        await api.crossfadeNow()
      } else {
        await api.load(trackId)
        await api.play()
      }
    } catch { /* surfaced by the status not changing */ }
    refreshStatus()
  }, [refreshStatus])

  const connClass =
    live.conn === 'connected' ? 'w-conn w-ok'
    : live.conn === 'failed' ? 'w-conn w-bad'
    : 'w-conn'

  return (
    <div className={theme}>
      <div className="w-titlebar">
        <span className="w-app">Y2K Music Server — Admin</span>
        <span className="w-spacer" style={{ flex: 1 }} />
        <label className="w-check" style={{ gap: 4, fontWeight: 'normal' }}>
          <span>Theme:</span>
          <select className="w-theme-select" value={theme} title="Admin UI theme"
            onChange={e => setTheme(e.target.value)}>
            {THEMES.map(([v, label]) => <option key={v} value={v}>{label}</option>)}
          </select>
        </label>
        <span className={connClass}>SignalR: {live.conn}</span>
      </div>

      <div className="w-menubar">
        <button disabled title="Menu actions land with later ships">File</button>
        <button onClick={() => setSettingsOpen(true)}>Settings</button>
        <button onClick={() => setLogOpen(o => !o)}>Log</button>
        {live.scan && (live.scan.state === 1 || live.scan.state === 2) && (
          <span className="w-muted" style={{ marginLeft: 'auto', alignSelf: 'center' }}>
            Scanning… {live.scan.filesProcessed}/{live.scan.filesFound}
          </span>
        )}
      </div>

      <div className="w-cols">
        <LibraryBrowser scan={live.scan} analysis={live.analysis} onPlayNow={playNow} />
        <div className="w-right-col">
          <DeckPanel live={live} status={status} refresh={refreshStatus}
            modes={mixModes} onToggleMode={toggleMode} />
          <PlaylistPanel />
        </div>
      </div>

      {logOpen && <LogPanel live={live} onClose={() => setLogOpen(false)} />}

      {settingsOpen && <SettingsDialog onClose={() => { setSettingsOpen(false); refreshModes() }} />}
    </div>
  )
}
