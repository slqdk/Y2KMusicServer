import { useCallback, useEffect, useRef, useState } from 'react'
import './jukebox.css'

/**
 * /jukebox — a stripped-down request page for a tablet left out at the party.
 *
 * Deliberately NOT a variant of the listener page: no rail, no themes, no
 * streaming controls, no album browse, no name gate. A guest walks up, types,
 * and presses ØNSK. Everything on screen is in Danish because the people using
 * it are guests, not operators.
 *
 * It shares the public API with the listener page and adds nothing server-side.
 */

type Track = {
  id: number
  title: string | null
  artist: string | null
  album: string | null
  durationSec: number
}

type QueueRow = {
  position: number
  trackId: number
  title: string | null
  artist: string | null
  durationSec: number
  /** 'Schedule' (Auto DJ), 'Manual' (the DJ picked it) or 'Request' (a guest). */
  source?: string | null
}

/** /api/playlists answers { id, name, count } — reading a "trackCount" that
 *  the endpoint never sends is why the chip showed a name with no number, and
 *  why a single playlist looked like a stray card. */
type PlaylistChip = { id: number; name: string; count: number; activeNow?: boolean }

const PAGE = 60

function fmt(sec: number): string {
  if (!sec || sec < 0) return ''
  const m = Math.floor(sec / 60)
  const s = Math.floor(sec % 60)
  return `${m}:${s.toString().padStart(2, '0')}`
}

/** The library stores plenty of tracks as "Artist - Title" in the title field
 *  with no artist tag. Same split the listener page uses. */
function split(artist: string | null | undefined, title: string | null | undefined) {
  const t = (title ?? '').trim()
  const a = (artist ?? '').trim()
  if (a) return { artist: a, title: t }
  const dash = t.indexOf(' - ')
  if (dash > 0) return { artist: t.slice(0, dash).trim(), title: t.slice(dash + 3).trim() }
  return { artist: '', title: t }
}

/** Stable per-tablet id so the server's request throttle can tell devices
 *  apart, and so the DJ sees which tablet asked. Nobody types a name here. */
function deviceId(): string {
  const key = 'y2k-jukebox-device'
  let id = localStorage.getItem(key)
  if (!id) {
    id = 'jb-' + Math.random().toString(36).slice(2, 10)
    localStorage.setItem(key, id)
  }
  return id
}

export default function Jukebox() {
  const [q, setQ] = useState('')
  const [results, setResults] = useState<Track[]>([])
  const [shown, setShown] = useState(PAGE)
  const [total, setTotal] = useState(0)
  const [busy, setBusy] = useState(false)

  const [chips, setChips] = useState<PlaylistChip[]>([])
  const [chipId, setChipId] = useState<number | null>(null)

  const [queue, setQueue] = useState<QueueRow[]>([])
  const [banner, setBanner] = useState('')
  // Request cooldown. `until` is a wall-clock deadline so a backgrounded tablet
  // resumes with the right number rather than a frozen one; `total` is what the
  // progress bar measures against.
  const [cool, setCool] = useState<{ until: number; total: number } | null>(null)
  const [, tick] = useState(0)
  const [canSkip, setCanSkip] = useState(false)
  const [nameRequired, setNameRequired] = useState(false)

  const debounce = useRef<number | undefined>(undefined)
  // Every search gets a number; only the newest one is allowed to write the
  // results. Tapping a chip while the default "newest" query is still in flight
  // otherwise ends with the slower reply landing last and replacing the
  // playlist the guest just opened — the list appearing and vanishing again.
  const reqSeq = useRef(0)
  const bannerTimer = useRef<number | undefined>(undefined)

  // One timer drives the countdown text and the bar.
  useEffect(() => {
    if (!cool) return
    const id = window.setInterval(() => {
      if (Date.now() >= cool.until) setCool(null)
      else tick(n => n + 1)
    }, 250)
    return () => window.clearInterval(id)
  }, [cool])

  const coolLeftSec = cool ? Math.max(0, Math.ceil((cool.until - Date.now()) / 1000)) : 0
  const coolPct = cool && cool.total > 0
    ? Math.min(100, Math.max(0, ((cool.total - coolLeftSec) / cool.total) * 100))
    : 0

  const say = useCallback((text: string) => {
    setBanner(text)
    window.clearTimeout(bannerTimer.current)
    bannerTimer.current = window.setTimeout(() => setBanner(''), 4000)
  }, [])

  // ── Live state: chips, queue, gates ────────────────────────────────────
  // This tablet is left running all evening, so nothing here may be a one-shot
  // fetch. Timeslots open and close, the DJ switches playlists off, the skip
  // gate unlocks — a page that read all that once at load would go on offering
  // music that is no longer in rotation.
  const loadLive = useCallback(() => {
      fetch('/api/playlists')
        .then(r => r.ok ? r.json() : null)
        // Only what Auto DJ is drawing from right now. Offering a guest a
        // playlist the DJ has switched off invites requests for music that was
        // deliberately taken out of tonight's rotation.
        .then(d => {
          const live = Array.isArray(d?.playlists)
            ? (d.playlists as PlaylistChip[]).filter(p => p.activeNow !== false)
            : []
          setChips(live)

          // If the playlist being browsed has just gone out of rotation, don't
          // strand the guest inside it: step back to the default view and say
          // why, rather than leaving a list they can no longer see the origin of.
          setChipId(cur => {
            if (cur == null || live.some(p => p.id === cur)) return cur
            say('Den afspilningsliste er ikke aktiv længere')
            return null
          })
        })
        .catch(() => { /* keep the last good chips */ })

      fetch('/api/playlist')
        .then(r => r.ok ? r.json() : [])
        .then(d => setQueue(Array.isArray(d) ? d.slice(0, 4) : []))
        .catch(() => { /* keep the last good list */ })

      fetch('/api/nowplaying')
        .then(r => r.ok ? r.json() : null)
        .then(d => {
          setNameRequired(!!d?.requireName)
          // allowNext already carries the operator's switch AND the timed gate,
          // so the button simply appears when skipping is genuinely allowed.
          setCanSkip(!!d?.allowNext)
        })
        .catch(() => { /* leave as-is */ })
  }, [say])

  useEffect(() => {
    loadLive()
    const id = window.setInterval(loadLive, 5000)
    return () => window.clearInterval(id)
  }, [loadLive])

  // ── Search / browse ────────────────────────────────────────────────────
  useEffect(() => {
    window.clearTimeout(debounce.current)
    const term = q.trim()

    debounce.current = window.setTimeout(() => {
      const params = new URLSearchParams()
      if (term) {
        params.set('q', term)
        params.set('take', '0')        // 0 = everything that matched
      } else if (chipId != null) {
        params.set('playlist', String(chipId))
        params.set('take', '200')      // browse paths want a real number
      } else {
        // Nothing typed and no playlist chosen: show what the library learned
        // about most recently rather than an empty screen. A guest who walks up
        // to the tablet has something to press immediately.
        params.set('newest', 'true')
        params.set('take', '40')
      }

      const mine = ++reqSeq.current
      setBusy(true)
      fetch(`/api/search?${params.toString()}`)
        .then(r => r.ok ? r.json() : { items: [] })
        .then(d => {
          if (mine !== reqSeq.current) return          // a newer query has won
          setResults(Array.isArray(d?.items) ? d.items : [])
          setTotal(d?.total ?? (d?.items?.length ?? 0))
          setShown(PAGE)
        })
        .catch(() => { if (mine === reqSeq.current) { setResults([]); setTotal(0) } })
        .finally(() => { if (mine === reqSeq.current) setBusy(false) })
    }, 300)

    return () => window.clearTimeout(debounce.current)
  }, [q, chipId])

  const request = async (t: Track) => {
    const d = split(t.artist, t.title)
    try {
      const r = await fetch('/api/request', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ trackId: t.id, deviceId: deviceId() })
      })

      if (r.status === 429) {
        // The throttle is per device, so on a shared tablet this is the whole
        // room's limit, not one guest's.
        const j = await r.json().catch(() => null)
        const sec = j?.retryAfterSec ?? 60
        setCool({ until: Date.now() + sec * 1000, total: j?.totalSec > 0 ? j.totalSec : sec })
        say(`Vent lidt – næste ønske om ${Math.max(1, Math.ceil(sec / 60))} min.`)
        return
      }
      if (!r.ok) { say('Ønsket kunne ikke sendes'); return }

      const j = await r.json().catch(() => null)
      // The server hands back the cooldown on success too, so the buttons dim
      // immediately rather than after the next rejected press.
      if (j?.cooldownSec > 0) setCool({ until: Date.now() + j.cooldownSec * 1000, total: j.cooldownSec })

      // Auto-accepted requests are in the queue already; pull the panel now so
      // the guest SEES their song arrive instead of waiting out the poll and
      // wondering whether the press worked.
      loadLive()

      say(j?.accepted
        ? `“${d.title}” er sat i kø`
        : `“${d.title}” er ønsket – DJ'en ser den`)
    } catch {
      say('Ingen forbindelse til serveren')
    }
  }

  const clearAll = () => { setQ(''); setChipId(null); setResults([]); setTotal(0) }

  const visible = results.slice(0, shown)
  const browsing = !q.trim() && chipId != null
  const chipName = chips.find(c => c.id === chipId)?.name

  return (
    <div className="jb">
      <header className="jb-head">
        <h1 className="jb-title">ØNSK DIN MUSIK</h1>

        <div className="jb-searchrow">
          <div className="jb-searchwrap">
            <span className="jb-searchicon" aria-hidden="true">🔍</span>
            <input
              className="jb-search"
              type="search"
              value={q}
              autoComplete="off"
              spellCheck={false}
              placeholder="Søg efter sang, kunstner eller album…"
              onChange={e => { setQ(e.target.value); if (e.target.value.trim()) setChipId(null) }}
            />
          </div>
          <button className="jb-clear" onClick={clearAll} disabled={!q && chipId == null}>
            Ryd
          </button>
        </div>

        {cool ? (
          <div className="jb-cool">
            <div className="jb-cool-bar"><span style={{ width: `${coolPct}%` }} /></div>
            <span className="jb-cool-text">
              Næste ønske om {Math.floor(coolLeftSec / 60)}:{(coolLeftSec % 60).toString().padStart(2, '0')}
            </span>
          </div>
        ) : (
          <p className="jb-hint">Find en sang og tryk <strong>ØNSK</strong> – så klarer vi resten</p>
        )}
      </header>

      <div className="jb-body">
        <main className="jb-main">
          {/* The page assumes the name requirement is off. If it is on, requests
              from here would be rejected, so say that plainly rather than
              letting guests press a button that cannot work. */}
          {nameRequired && (
            <div className="jb-warn">
              Ønsker er slået fra på denne skærm: kravet om navn er tændt i indstillingerne.
              Brug gæstesiden i stedet, eller slå “Kræv navn” fra.
            </div>
          )}

          {!q.trim() && chipId == null && chips.length > 0 && (
            <>
              <h2 className="jb-sect">FORSLAG TIL DIG</h2>
              <div className="jb-chips">
                {chips.map(c => (
                  <button key={c.id} className="jb-chip" onClick={() => setChipId(c.id)}>
                    <span className="jb-chip-name">{c.name}</span>
                    <span className="jb-chip-count">{c.count} numre</span>
                  </button>
                ))}
              </div>
            </>
          )}

          <h2 className="jb-sect">
              {browsing ? (chipName ?? 'Afspilningsliste')
                : q.trim() ? 'RESULTATER'
                  : 'NYESTE NUMRE'}
              {total > 0 && <span className="jb-count"> · {total}</span>}
              {browsing && (
                <button className="jb-back" onClick={() => setChipId(null)}>← Tilbage</button>
              )}
          </h2>

          {browsing && (
            <button className="jb-backbig" onClick={() => setChipId(null)}>
              ← Tilbage til forslag
            </button>
          )}

          {busy && visible.length === 0 && <p className="jb-empty">Søger…</p>}
          {!busy && visible.length === 0 && (
            <p className="jb-empty">
              {q.trim() ? 'Ingen sange fundet. Prøv et andet ord.' : 'Ingen numre at vise endnu.'}
            </p>
          )}

          <ul className="jb-list">
            {visible.map(t => {
              const d = split(t.artist, t.title)
              return (
                <li key={t.id} className="jb-row">
                  <img
                    className="jb-art"
                    src={`/api/albumart?trackId=${t.id}`}
                    alt=""
                    loading="lazy"
                    onError={e => { (e.currentTarget as HTMLImageElement).style.visibility = 'hidden' }}
                  />
                  <div className="jb-rowmain">
                    <div className="jb-song">{d.title}</div>
                    <div className="jb-artist">{d.artist || '—'}</div>
                  </div>
                  <span className="jb-dur">{fmt(t.durationSec)}</span>
                  <button
                    className="jb-req"
                    disabled={nameRequired || !!cool}
                    title={cool ? 'Vent til nedtællingen er slut' : 'Ønsk denne sang'}
                    onClick={() => request(t)}
                  >
                    ØNSK
                  </button>
                </li>
              )
            })}
          </ul>

          {results.length > shown && (
            <button className="jb-more" onClick={() => setShown(s => s + PAGE)}>
              Vis flere ({results.length - shown})
            </button>
          )}
        </main>

        <aside className="jb-side">
          <h2 className="jb-sidehead">NÆSTE NUMRE</h2>
          {/* The panel lists the next four whatever put them there, so the old
              subtitle claimed every one of them was a guest request. Say what
              it really is, and mark the ones that ARE requests. */}
          <p className="jb-sidesub">Sådan spiller vi videre</p>

          <ol className="jb-queue">
            {queue.map((r, i) => {
              const d = split(r.artist, r.title)
              return (
                <li key={`${r.position}-${r.trackId}`} className="jb-qrow">
                  <span className="jb-qnum">{i + 1}</span>
                  <img
                    className="jb-qart"
                    src={`/api/albumart?trackId=${r.trackId}`}
                    alt=""
                    loading="lazy"
                    onError={e => { (e.currentTarget as HTMLImageElement).style.visibility = 'hidden' }}
                  />
                  <div className="jb-qmain">
                    <div className="jb-qsong">{d.title}</div>
                    <div className="jb-qartist">
                      {d.artist || '—'}
                      {r.source === 'Request' && <span className="jb-qtag">ØNSKET</span>}
                    </div>
                  </div>
                </li>
              )
            })}
            {queue.length === 0 && <li className="jb-empty">Køen er tom lige nu.</li>}
          </ol>

          <div className="jb-lock">🔒 Køen styres automatisk</div>
        </aside>
      </div>

      {/* Skip lives in the corner, out of the way of the request flow, and only
          exists once the operator's timed gate has opened. */}
      {canSkip && (
        <button
          className="jb-skip"
          onClick={async () => {
            // Hide it immediately. allowNext only refreshes on the 5s poll, so
            // the button otherwise sits there looking pressable for several
            // seconds after the skip has already happened — long enough for a
            // second guest to skip the song that just started.
            setCanSkip(false)
            try {
              const r = await fetch('/api/next', { method: 'POST' })
              say(r.ok ? 'Skifter til næste sang…' : 'Kan ikke skippe lige nu')
            } catch { say('Ingen forbindelse til serveren') }
          }}
        >
          ⏭ Næste sang
        </button>
      )}

      {banner && <div className="jb-toast">✓ {banner}</div>}
    </div>
  )
}
