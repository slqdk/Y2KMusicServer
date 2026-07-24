import { useEffect, useState } from 'react'
import * as api from './api'

const BITRATES = [64, 128, 192, 320]

export default function SettingsDialog({ onClose }: { onClose: () => void }) {
  const [s, setS] = useState<api.SettingsDto | null>(null)
  const [autodj, setAutodj] = useState<api.AutoDjSettings | null>(null)
  const [stream, setStream] = useState<api.StreamStatus | null>(null)
  const [mix, setMix] = useState<api.MixRulesDto | null>(null)
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [err, setErr] = useState<string | null>(null)

  // YouTube preflight (its own endpoint; runs on demand, persists nothing).
  const [ytResult, setYtResult] = useState<api.YouTubeCheckResult | null>(null)
  const [ytBusy, setYtBusy] = useState(false)
  const [ytErr, setYtErr] = useState<string | null>(null)
  const [ytEnabled, setYtEnabled] = useState<boolean | null>(null)
  const [ytMaxMB, setYtMaxMB] = useState<number | null>(null)
  const [ytMaxAgeDays, setYtMaxAgeDays] = useState<number | null>(null)
  const [ytCache, setYtCache] = useState<api.WebCacheStats | null>(null)
  const [ytClearing, setYtClearing] = useState(false)
  const [ytClearMsg, setYtClearMsg] = useState<string | null>(null)

  useEffect(() => {
    api.getSettings().then(setS).catch(() => setErr('Could not load settings.'))
    api.getAutoDj().then(setAutodj).catch(() => {})
    api.getStream().then(setStream).catch(() => {})
    api.getMixRules().then(setMix).catch(() => {})
    api.getYouTubeSettings().then(s => {
      setYtEnabled(s.enabled); setYtMaxMB(s.cacheMaxMB); setYtMaxAgeDays(s.cacheMaxAgeDays)
    }).catch(() => setYtEnabled(false))
    api.getYouTubeCache().then(setYtCache).catch(() => {})
  }, [])

  const patch = (p: Partial<api.SettingsDto>) => setS(prev => (prev ? { ...prev, ...p } : prev))

  // Auto DJ and streaming keep their own endpoints (the Settings PUT excludes
  // them), so they apply immediately rather than via the Save button.
  const apply = async (fn: () => Promise<unknown>) => {
    setBusy(true)
    try { await fn() } catch { /* surfaced via control state */ } finally { setBusy(false) }
  }

  // Auto-mix rules live in their own store (mixrules.json) and likewise apply
  // immediately. The PUT replaces the whole object, so merge the change in.
  const applyMix = (p: Partial<api.MixRulesDto>) =>
    apply(async () => { if (mix) setMix(await api.putMixRules({ ...mix, ...p })) })

  const save = async () => {
    if (!s) return
    setBusy(true); setSaved(false); setErr(null)
    try {
      const r = await api.putSettings({
        nextTriggerPct: s.nextTriggerPct, nextFadeSeconds: s.nextFadeSeconds,
        normalizeEnabled: s.normalizeEnabled, limiterEnabled: s.limiterEnabled,
        targetLufs: s.targetLufs, volume: s.volume, scanWorkers: s.scanWorkers,
        allowWebNext: s.allowWebNext, showWebCategories: s.showWebCategories,
        debugLogging: s.debugLogging,
        showListenLive: s.showListenLive, requestLimitEnabled: s.requestLimitEnabled,
        requestIntervalMinutes: s.requestIntervalMinutes, autoAcceptRequests: s.autoAcceptRequests
      })
      setS(r); setSaved(true)
    } catch { setErr('Save failed.') } finally { setBusy(false) }
  }

  // Runs the YouTube tool-stack preflight. The check can take up to ~a minute
  // (the live dry-run extract), so the button shows a working state throughout.
  const runYtCheck = async () => {
    setYtBusy(true); setYtErr(null); setYtResult(null)
    try { setYtResult(await api.checkYouTube()) }
    catch { setYtErr('Check failed to run.') }
    finally { setYtBusy(false) }
  }

  // The fetch on/off gate lives in its own store (integrations.json) and applies
  // immediately, like the Auto DJ / streaming toggles.
  const toggleYt = async (on: boolean) => {
    setYtEnabled(on)
    try { setYtEnabled((await api.setYouTubeSettings({ enabled: on })).enabled) }
    catch { setYtEnabled(!on) }
  }

  // Persist both caps together (backend merges the update); 0 = unlimited / off.
  const saveCaps = async () => {
    try {
      const s = await api.setYouTubeSettings({
        cacheMaxMB: ytMaxMB ?? 0, cacheMaxAgeDays: ytMaxAgeDays ?? 0,
      })
      setYtMaxMB(s.cacheMaxMB); setYtMaxAgeDays(s.cacheMaxAgeDays)
    } catch { /* keep the typed value */ }
  }

  const clearCache = async () => {
    setYtClearing(true); setYtClearMsg(null)
    try {
      const r = await api.clearYouTubeCache()
      setYtClearMsg(`Removed ${r.removed}, freed ${(r.freedBytes / 1048576).toFixed(1)} MB`)
      api.getYouTubeCache().then(setYtCache).catch(() => {})
    } catch { setYtClearMsg('Clear failed.') }
    finally { setYtClearing(false) }
  }

  // Overall banner: red on any critical failure, amber if only optional stages
  // failed (still usable), green when everything passed.
  const ytSummary = ytResult
    ? (!ytResult.ok
        ? { cls: 'w-err', txt: 'Problems found' }
        : ytResult.steps.some(st => !st.ok && !st.critical)
          ? { cls: 'w-yt-warntext', txt: 'Ready — with warnings' }
          : { cls: 'w-yt-pass', txt: 'All checks passed' })
    : null

  return (
    <div className="w-overlay" onMouseDown={onClose}>
      <div className="w-dialog w-raised" onMouseDown={e => e.stopPropagation()}>
        <div className="w-titlebar">
          <span className="w-app">Settings</span>
          <span style={{ flex: 1 }} />
          <button className="w-btn" onClick={onClose} style={{ minHeight: 16, padding: '0 7px' }}>✕</button>
        </div>

        <div className="w-dialog-body">
          {!s && <div className="w-muted">Loading…</div>}
          {s && (
            <>
              <fieldset className="w-group">
                <legend>Next Button &amp; Auto DJ</legend>
                <label className="w-check">
                  <input type="checkbox" checked={autodj?.autoDj ?? false} disabled={!autodj || busy}
                    onChange={e => apply(async () => setAutodj(await api.setAutoDj({ on: e.target.checked })))} />
                  Auto DJ
                </label>
                <div className="w-toolbar">
                  <label>Top-up tracks:</label>
                  <input type="number" min={1} max={20} style={{ width: 56 }}
                    value={autodj?.tracks ?? 3} disabled={!autodj || busy}
                    onChange={e => apply(async () => setAutodj(await api.setAutoDj({ tracks: Number(e.target.value) })))} />
                  <label>BPM range:</label>
                  <input type="number" min={0} max={50} style={{ width: 56 }}
                    value={autodj?.bpmDev ?? 5} disabled={!autodj || busy}
                    onChange={e => apply(async () => setAutodj(await api.setAutoDj({ bpmDev: Number(e.target.value) })))} />
                  <span className="w-muted">0 = random</span>
                  <span className="w-spacer" />
                  <button className="w-btn" disabled={busy} onClick={() => apply(() => api.fillAutoDj())}>Fill now</button>
                </div>
                <div className="w-muted" style={{ marginTop: 4 }}>
                  Selection matches within the BPM range once tracks are analysed; tracks without BPM yet fall back to random. Auto DJ and streaming apply immediately.
                </div>
              </fieldset>

              <fieldset className="w-group">
                <legend>Live Streaming</legend>
                <div className="w-toolbar">
                  <span>The /stream broadcast is always on.</span>
                  <label>Bitrate:</label>
                  <select value={stream?.bitrate ?? 128} disabled={!stream || busy}
                    onChange={e => apply(async () => setStream(await api.setStreamBitrate(Number(e.target.value))))}>
                    {BITRATES.map(b => <option key={b} value={b}>{b} kbps</option>)}
                  </select>
                  <span className="w-spacer" />
                  <span className="w-muted">{stream ? `${stream.listeners} listener(s)` : '—'}</span>
                </div>
              </fieldset>

              <fieldset className="w-group">
                <legend>Playback</legend>
                <label className="w-check"><input type="checkbox" checked={s.normalizeEnabled} onChange={e => patch({ normalizeEnabled: e.target.checked })} /> Normalize volume</label>
                <label className="w-check"><input type="checkbox" checked={s.limiterEnabled} onChange={e => patch({ limiterEnabled: e.target.checked })} /> Limiter (anti-clip)</label>
                <div className="w-formrow">
                  <label>Target loudness (LUFS):</label>
                  <input type="number" min={-40} max={0} step={1} value={s.targetLufs}
                    onChange={e => patch({ targetLufs: Number(e.target.value) })} style={{ width: 64 }} />
                </div>
              </fieldset>

              <fieldset className="w-group">
                <legend>Next transition</legend>
                <div className="w-formrow">
                  <label>Start next at:</label>
                  <input type="number" min={5} max={95} value={s.nextTriggerPct}
                    onChange={e => patch({ nextTriggerPct: Number(e.target.value) })} style={{ width: 64 }} /> %
                </div>
                <div className="w-formrow">
                  <label>Normal crossfade:</label>
                  <input type="number" min={0} max={30} value={s.nextFadeSeconds}
                    onChange={e => patch({ nextFadeSeconds: Number(e.target.value) })} style={{ width: 64 }} /> sec
                  <span className="w-muted">caps a Normal (un-aligned) crossfade</span>
                </div>
                <div className="w-formrow">
                  <label>Beat-matched (same tempo):</label>
                  <input type="number" min={1} max={16} step={1} value={mix?.sameTempoBars ?? 4}
                    disabled={!mix || busy}
                    onChange={e => applyMix({ sameTempoBars: Number(e.target.value) })} style={{ width: 64 }} /> bars
                </div>
                <div className="w-formrow">
                  <label>Beat-matched (related tempo):</label>
                  <input type="number" min={1} max={16} step={1} value={mix?.relatedTempoBars ?? 2}
                    disabled={!mix || busy}
                    onChange={e => applyMix({ relatedTempoBars: Number(e.target.value) })} style={{ width: 64 }} /> bars
                </div>
                <div className="w-muted" style={{ marginTop: 2 }}>
                  Normal crossfades use the seconds cap; beat-matched crossfades and the moves use bars.
                </div>
              </fieldset>

              <fieldset className="w-group">
                <legend>Mixing</legend>
                <label className="w-check">
                  <input type="checkbox" checked={mix?.mixingAuto ?? false} disabled={!mix || busy}
                    onChange={e => applyMix({ mixingAuto: e.target.checked })} />
                  Auto-pick a mixing move per pair
                </label>
                <div className="w-muted" style={{ margin: '2px 0 6px' }}>
                  When on, the Mixing section picks a move — vocal-tease, bass-swap or bass-breakdown — whenever the pair allows, and otherwise falls back to the chosen crossfade. Mirrors the deck panel&apos;s Mixing toggle; applies immediately.
                </div>
                <div className="w-mode-label" style={{ margin: '0 0 4px' }}>Mixing may use:</div>
                <label className="w-check">
                  <input type="checkbox" checked={mix?.vocalTease ?? false} disabled={!mix || busy || !mix.mixingAuto}
                    onChange={e => applyMix({ vocalTease: e.target.checked })} />
                  Vocal tease — ride B&apos;s vocal in over A&apos;s instrumental tail
                </label>
                <label className="w-check">
                  <input type="checkbox" checked={mix?.bassSwap ?? false} disabled={!mix || busy || !mix.mixingAuto}
                    onChange={e => applyMix({ bassSwap: e.target.checked })} />
                  Bass swap — hand the low end from A to B on a downbeat
                </label>
                <label className="w-check">
                  <input type="checkbox" checked={mix?.bassBreakdown ?? false} disabled={!mix || busy || !mix.mixingAuto}
                    onChange={e => applyMix({ bassBreakdown: e.target.checked })} />
                  Bass breakdown — strip A to its bassline as B comes in
                </label>
                <div className="w-formrow">
                  <label>BPM tolerance:</label>
                  <input type="number" min={0} max={20} step={1} value={mix?.bpmTolerance ?? 5}
                    disabled={!mix || busy}
                    onChange={e => applyMix({ bpmTolerance: Number(e.target.value) })} style={{ width: 64 }} /> BPM
                  <span className="w-muted">beat-match window (Crossfade &amp; Mixing)</span>
                </div>
                <div className="w-formrow">
                  <label>Deck B entry level:</label>
                  <input type="number" min={0} max={1} step={0.05} value={mix?.deckBEntryLevel ?? 0.8}
                    disabled={!mix || busy || !mix.mixingAuto}
                    onChange={e => applyMix({ deckBEntryLevel: Number(e.target.value) })} style={{ width: 64 }} />
                  <span className="w-muted">0–1, how loud B comes in</span>
                </div>
                <div className="w-formrow">
                  <label>Bass hold:</label>
                  <input type="number" min={0} max={32} step={1} value={mix?.bassHoldBars ?? 4}
                    disabled={!mix || busy || !mix.mixingAuto || !mix.bassSwap}
                    onChange={e => applyMix({ bassHoldBars: Number(e.target.value) })} style={{ width: 64 }} /> bars
                  <span className="w-muted">cut held before the bass-swap</span>
                </div>
                <div className="w-formrow">
                  <label>Max overlap:</label>
                  <input type="number" min={1} max={32} step={1} value={mix?.maxOverlapBars ?? 8}
                    disabled={!mix || busy}
                    onChange={e => applyMix({ maxOverlapBars: Number(e.target.value) })} style={{ width: 64 }} /> bars
                  <span className="w-muted">caps a beat-matched crossfade</span>
                </div>
              </fieldset>

              <fieldset className="w-group">
                <legend>Web requests</legend>
                <label className="w-check"><input type="checkbox" checked={s.allowWebNext} onChange={e => patch({ allowWebNext: e.target.checked })} /> Allow website visitors to skip to next song</label>
                <label className="w-check"><input type="checkbox" checked={s.autoAcceptRequests} onChange={e => patch({ autoAcceptRequests: e.target.checked })} /> Auto-accept requests (skip the approve step)</label>
                <label className="w-check"><input type="checkbox" checked={s.showWebCategories} onChange={e => patch({ showWebCategories: e.target.checked })} /> Show music filters on website (genre / decade)</label>
                <label className="w-check"><input type="checkbox" checked={s.showListenLive} onChange={e => patch({ showListenLive: e.target.checked })} /> Show &ldquo;Listen Live&rdquo; button on website</label>
                <label className="w-check"><input type="checkbox" checked={s.requestLimitEnabled} onChange={e => patch({ requestLimitEnabled: e.target.checked })} /> Limit how often a device can request a song</label>
                <div className="w-formrow">
                  <label>Minutes between requests:</label>
                  <input type="number" min={1} max={1440} value={s.requestIntervalMinutes} disabled={!s.requestLimitEnabled}
                    onChange={e => patch({ requestIntervalMinutes: Number(e.target.value) })} style={{ width: 64 }} />
                  <span className="w-muted">per device</span>
                </div>
              </fieldset>

              <fieldset className="w-group">
                <legend>YouTube integration</legend>
                <label className="w-check">
                  <input type="checkbox" checked={ytEnabled ?? false} disabled={ytEnabled === null}
                    onChange={e => toggleYt(e.target.checked)} />
                  Enable YouTube fetch (search &amp; play tracks not in your library)
                </label>
                <div className="w-muted" style={{ margin: '0 0 6px' }}>
                  Play tracks that aren&apos;t in your library by fetching them from YouTube
                  (downloaded to a local cache, then mixed like any other track). Not switched on
                  yet — this tests that the yt-dlp tool stack works in the service&apos;s own
                  process context before it is enabled. It downloads nothing.
                </div>
                <div className="w-toolbar">
                  <button className="w-btn" disabled={ytBusy} onClick={runYtCheck}>
                    {ytBusy ? 'Testing…' : 'Test integration'}
                  </button>
                  {ytSummary &&
                    <span className={ytSummary.cls}>{ytSummary.txt} ({ytResult!.elapsedMs} ms)</span>}
                  {ytErr && <span className="w-err">{ytErr}</span>}
                </div>
                {ytBusy && (
                  <div className="w-muted" style={{ marginTop: 4 }}>
                    Running the preflight — the live extraction step can take up to a minute…
                  </div>
                )}
                {ytResult && (
                  <table className="w-yt-check">
                    <tbody>
                      {ytResult.steps.map(st => (
                        <tr key={st.name}>
                          <td className="w-yt-status">
                            <span className={'w-yt-dot ' +
                              (st.ok ? 'w-yt-ok' : st.critical ? 'w-yt-bad' : 'w-yt-warn')} />
                          </td>
                          <td className="w-yt-name">
                            {st.name}
                            {!st.ok && !st.critical && <span className="w-muted"> (optional)</span>}
                          </td>
                          <td className="w-yt-detail">
                            {st.detail}
                            {st.version && <div className="w-muted">{st.version}</div>}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}

                <div className="w-formrow" style={{ marginTop: 8 }}>
                  <span className="w-muted">
                    Cache: {ytCache
                      ? `${ytCache.trackCount} track(s), ${(ytCache.bytes / 1048576).toFixed(1)} MB` +
                        (ytCache.pinnedCount ? ` (${ytCache.pinnedCount} in use)` : '')
                      : '…'}
                  </span>
                  <span className="w-spacer" />
                  <button className="w-btn" disabled={ytClearing} onClick={clearCache}>
                    {ytClearing ? 'Clearing…' : 'Clear cache'}
                  </button>
                </div>
                {ytClearMsg && <div className="w-muted" style={{ marginBottom: 4 }}>{ytClearMsg}</div>}
                <div className="w-formrow">
                  <label>Max cache size (MB, 0 = unlimited):</label>
                  <input type="number" min={0} value={ytMaxMB ?? 0} style={{ width: 72 }}
                    disabled={ytMaxMB === null}
                    onChange={e => setYtMaxMB(Math.max(0, Number(e.target.value) || 0))}
                    onBlur={saveCaps} />
                </div>
                <div className="w-formrow">
                  <label>Max cache age (days, 0 = off):</label>
                  <input type="number" min={0} value={ytMaxAgeDays ?? 0} style={{ width: 72 }}
                    disabled={ytMaxAgeDays === null}
                    onChange={e => setYtMaxAgeDays(Math.max(0, Number(e.target.value) || 0))}
                    onBlur={saveCaps} />
                </div>
              </fieldset>

              <fieldset className="w-group">
                <legend>System</legend>
                <div className="w-formrow">
                  <label>Volume:</label>
                  <input type="range" min={0} max={100} value={s.volume}
                    onChange={e => patch({ volume: Number(e.target.value) })} style={{ flex: 1 }} />
                  <span style={{ width: 36, textAlign: 'right' }}>{s.volume}%</span>
                </div>
                <div className="w-formrow">
                  <label>Scan workers:</label>
                  <input type="number" min={1} max={16} value={s.scanWorkers}
                    onChange={e => patch({ scanWorkers: Number(e.target.value) })} style={{ width: 64 }} />
                </div>
                <label className="w-check"><input type="checkbox" checked={s.debugLogging} onChange={e => patch({ debugLogging: e.target.checked })} /> Debug logging</label>
              </fieldset>

              <div className="w-muted" style={{ margin: '4px 0' }}>
                Mixing, Next, and Volume apply to the next track loaded / next transition — not the deck playing right now.
                Limiter and the LUFS target are stored but inert until the Phase 5 loudness analysis lands.
              </div>

              <div className="w-toolbar">
                <button className="w-btn w-primary" disabled={busy} onClick={save}>Save settings</button>
                {saved && <span className="w-muted">Saved.</span>}
                {err && <span className="w-err">{err}</span>}
              </div>
            </>
          )}
          {err && !s && <div className="w-err">{err}</div>}
        </div>
      </div>
    </div>
  )
}
