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

  useEffect(() => {
    api.getSettings().then(setS).catch(() => setErr('Could not load settings.'))
    api.getAutoDj().then(setAutodj).catch(() => {})
    api.getStream().then(setStream).catch(() => {})
    api.getMixRules().then(setMix).catch(() => {})
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
        requestIntervalMinutes: s.requestIntervalMinutes
      })
      setS(r); setSaved(true)
    } catch { setErr('Save failed.') } finally { setBusy(false) }
  }

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
                  <label className="w-check">
                    <input type="checkbox" checked={stream?.enabled ?? false} disabled={!stream || busy}
                      onChange={e => apply(async () => setStream(await api.setStreamEnabled(e.target.checked)))} />
                    Enable MP3 stream (/stream)
                  </label>
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
                <label className="w-check"><input type="checkbox" checked={s.showWebCategories} onChange={e => patch({ showWebCategories: e.target.checked })} /> Show category selector on website</label>
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
