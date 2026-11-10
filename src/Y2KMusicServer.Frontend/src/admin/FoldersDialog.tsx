import { useEffect, useState } from 'react'
import * as api from './api'
import FolderBrowser from './FolderBrowser'

/**
 * The global scan-folder list — the one place music folders are assigned
 * (replaces the per-category folder dialogs). Adding a folder scans it
 * automatically; per-folder Rescan / Clear data / Remove mirror the old
 * category-folder actions with the same innermost-folder-wins ownership.
 */
export default function FoldersDialog({ onClose, onChanged }:
  { onClose: () => void; onChanged: () => void }) {

  const [folders, setFolders] = useState<api.ScanFolderDto[]>([])
  const [newPath, setNewPath] = useState('')
  const [busy, setBusy] = useState(false)
  // Which field the folder browser is filling: the Add box, or the YouTube
  // download destination below the list.
  const [browsing, setBrowsing] = useState<'add' | 'youtube' | 'relocate' | null>(null)
  // The YouTube download destination. Server-resolved, so a blank stored value
  // still shows the default path it will actually use; ytNote carries the
  // advisory when the folder sits inside (or around) a Music folder.
  // ytFolder is the STORED value (blank = the default applies), so the box being
  // empty is honest rather than pre-filled with a default that Set would then
  // write back verbatim. ytEffective is what downloads use right now, shown as
  // the placeholder and after a save.
  const [ytFolder, setYtFolder] = useState('')
  const [ytEffective, setYtEffective] = useState('')
  const [ytNote, setYtNote] = useState<string | null>(null)
  const [ytSaving, setYtSaving] = useState(false)
  const [msg, setMsg] = useState<string | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [confirm, setConfirm] = useState<{ folder: api.ScanFolderDto; kind: 'clear' | 'remove' } | null>(null)
  // Relocate: the music now lives somewhere else (new drive letter, new
  // machine, new share). preview is the dry run for exactly these two paths;
  // editing either field invalidates it, so Move only ever applies what was
  // checked.
  const [reloc, setReloc] = useState<{ from: string; to: string } | null>(null)
  const [relocPreview, setRelocPreview] = useState<api.RelocateResult | null>(null)
  const [relocBusy, setRelocBusy] = useState(false)

  const refresh = () => api.getScanFolders().then(r => setFolders(r.folders)).catch(() => {})
  useEffect(() => { refresh() }, [])

  useEffect(() => {
    api.getYouTubeSettings()
      .then(s => {
        setYtFolder(s.downloadFolderStored)
        setYtEffective(s.downloadFolder)
        setYtNote(s.folderWarning)
      })
      .catch(() => {})
  }, [])

  // Saved explicitly rather than on blur: this one moves where files land, so a
  // stray click shouldn't commit it. Already-downloaded files stay where they are.
  const saveYtFolder = async () => {
    setYtSaving(true); setErr(null); setMsg(null)
    try {
      const s = await api.setYouTubeSettings({ downloadFolder: ytFolder.trim() })
      setYtFolder(s.downloadFolderStored)
      setYtEffective(s.downloadFolder)
      setYtNote(s.folderWarning)
      // Report what the SERVER came back with, not what was typed — if the two
      // differ, the save didn't take and the message says so instead of implying
      // success.
      setMsg(s.downloadFolder === ytFolder.trim() || ytFolder.trim().length === 0
        ? `YouTube downloads will land in ${s.downloadFolder}`
        : `The server kept ${s.downloadFolder} — the new folder was not stored.`)
    } catch (e) {
      setErr(e instanceof api.ApiError
        ? `Could not save the YouTube folder (${e.message})`
        : 'Could not save the YouTube folder — no reply from the server.')
    } finally { setYtSaving(false) }
  }

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return
      if (confirm) setConfirm(null)
      else if (reloc) setReloc(null)
      else onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose, confirm, reloc])

  const act = async (fn: () => Promise<unknown>, done?: string) => {
    setBusy(true); setErr(null)
    try { await fn(); await refresh(); onChanged(); if (done) setMsg(done) }
    catch (e) { setErr(e instanceof api.ApiError ? e.message : 'The action failed.') }
    finally { setBusy(false) }
  }

  const add = () => {
    const p = newPath.trim()
    if (!p) return
    act(async () => { await api.addScanFolder(p); setNewPath('') }, 'Folder added — scanning…')
  }

  const openRelocate = (from: string) => {
    setReloc({ from, to: '' })
    setRelocPreview(null)
    setErr(null)
  }
  const editReloc = (patch: Partial<{ from: string; to: string }>) => {
    setReloc(r => (r ? { ...r, ...patch } : r))
    setRelocPreview(null)
  }
  const checkRelocate = async () => {
    if (!reloc) return
    setRelocBusy(true)
    try { setRelocPreview(await api.relocateLibrary(reloc.from, reloc.to, true)) }
    catch { setRelocPreview(null); setErr('Could not check that move — no reply from the server.') }
    finally { setRelocBusy(false) }
  }
  const runRelocate = async () => {
    if (!reloc) return
    setRelocBusy(true); setErr(null)
    try {
      const r = await api.relocateLibrary(reloc.from, reloc.to, false)
      if (r.error) { setRelocPreview(r); return }
      setReloc(null); setRelocPreview(null)
      await refresh(); onChanged()
      setMsg(`Moved ${r.tracks} track${r.tracks === 1 ? '' : 's'} to ${r.to}` +
        (r.duplicates > 0 ? ` (${r.duplicates} duplicate${r.duplicates === 1 ? '' : 's'} merged)` : '') +
        ' — playlists and history kept.')
    } catch { setErr('The move failed — no reply from the server.') }
    finally { setRelocBusy(false) }
  }
  const relocReady = !!relocPreview && !relocPreview.error && relocPreview.targetExists
    && relocPreview.from.toLowerCase() === (reloc?.from ?? '').trim().replace(/[\\/]+$/, '').toLowerCase()

  const runConfirm = () => {
    if (!confirm) return
    const { folder, kind } = confirm
    setConfirm(null)
    if (kind === 'clear')
      act(() => api.clearScanFolder(folder.id), `Cleared the tracks under ${folder.path}.`)
    else
      act(() => api.removeScanFolder(folder.id, true), `Removed ${folder.path} and its tracks.`)
  }

  return (
    <div className="w-overlay" onMouseDown={onClose}>
      <div className="w-dialog w-raised" onMouseDown={e => e.stopPropagation()}
        style={{ width: 680, maxWidth: '94vw' }}>
        <div className="w-titlebar">
          <span className="w-app">Music folders</span>
          <span style={{ flex: 1 }} />
          <button className="w-btn" onClick={onClose} style={{ minHeight: 16, padding: '0 7px' }}>✕</button>
        </div>

        <div className="w-dialog-body">
          <div className="w-muted" style={{ marginBottom: 6 }}>
            The library is built from these folders (subfolders included). Adding a folder scans it right away;
            new tracks land in the flat library and are filtered by Format / Genre / Decade.
          </div>

          <div className="w-toolbar">
            <input type="text" value={newPath} style={{ flex: 1 }} disabled={busy}
              onChange={e => setNewPath(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter') add() }}
              placeholder="C:\Music or \\server\share\Music" />
            <button className="w-btn" disabled={busy} onClick={() => setBrowsing('add')}>Browse…</button>
            <button className="w-btn" disabled={busy || !newPath.trim()} onClick={add}>Add</button>
          </div>

          <div className="w-listwrap w-sunken" style={{ maxHeight: 260, marginTop: 6 }}>
            <table className="w-table">
              <thead>
                <tr><th>Path</th><th className="w-num">Tracks</th><th style={{ width: 290 }} /></tr>
              </thead>
              <tbody>
                {folders.map(f => (
                  <tr key={f.id} style={f.active ? undefined : { opacity: .55 }}>
                    <td title={f.path} style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>
                      {f.path}{!f.exists && <span className="w-err"> (not reachable)</span>}
                      {!f.active && <span className="w-muted"> (hidden from search)</span>}
                    </td>
                    <td className="w-num">{f.trackCount}</td>
                    <td style={{ whiteSpace: 'nowrap' }}>
                      <button className="w-btn" disabled={busy}
                        title={f.active
                          ? 'Hide this folder’s tracks from search (playlists and playback are not affected)'
                          : 'Show this folder’s tracks in search again'}
                        onClick={() => act(() => api.setScanFolderActive(f.id, !f.active),
                          f.active ? `Hid ${f.path} from search.` : `${f.path} is searchable again.`)}>
                        {f.active ? 'On' : 'Off'}
                      </button>{' '}
                      <button className="w-btn" disabled={busy} title="Scan this folder for new files"
                        onClick={() => act(() => api.rescanScanFolder(f.id), 'Rescan queued.')}>↻</button>{' '}
                      <button className="w-btn" disabled={busy}
                        title="The music moved (new drive letter, new machine, new share): point this folder at its new location, keeping playlists and history"
                        onClick={() => openRelocate(f.path)}>Move…</button>{' '}
                      <button className="w-btn" disabled={busy} title="Remove this folder's tracks from the library (keeps the folder assigned)"
                        onClick={() => setConfirm({ folder: f, kind: 'clear' })}>Clear</button>{' '}
                      <button className="w-btn" disabled={busy} title="Remove the folder and its tracks"
                        onClick={() => setConfirm({ folder: f, kind: 'remove' })}>Remove</button>
                    </td>
                  </tr>
                ))}
                {folders.length === 0 && (
                  <tr><td colSpan={3} className="w-muted" style={{ padding: 8 }}>No folders yet — add your music folder above.</td></tr>
                )}
              </tbody>
            </table>
          </div>

          {folders.some(f => !f.exists) && (
            <div className="w-muted" style={{ marginTop: 4 }}>
              A folder that is <span className="w-err">not reachable</span> usually means the music
              moved — use <b>Move…</b> to point it at the new location. Don’t Clear or Remove it: that
              deletes its tracks from every playlist.
            </div>
          )}

          {/* The YouTube destination is a folder choice, so it lives here with the
              other folder choices rather than in Settings. It is NOT a scan
              folder: it is not in the list above, gets no Rescan / Clear buttons,
              and its tracks are indexed as they are downloaded. Putting it inside
              one of the folders above is allowed — the note says what that means. */}
          <fieldset className="w-group" style={{ marginTop: 10 }}>
            <legend>YouTube downloads</legend>
            <div className="w-muted" style={{ marginBottom: 6 }}>
              Where tracks pasted into the YouTube dialog are downloaded to, with tags and
              cover art, then added to the library. This folder is not scanned — downloads
              index themselves as they arrive.
            </div>
            <div className="w-toolbar">
              <input type="text" value={ytFolder} style={{ flex: 1 }} spellCheck={false}
                disabled={ytSaving}
                onChange={e => setYtFolder(e.target.value)}
                onKeyDown={e => { if (e.key === 'Enter') saveYtFolder() }}
                placeholder={ytEffective || 'C:\\ProgramData\\Y2KMusicServer\\youtube'} />
              <button className="w-btn" disabled={ytSaving} onClick={() => setBrowsing('youtube')}>Browse…</button>
              <button className="w-btn" disabled={ytSaving} onClick={saveYtFolder}>
                {ytSaving ? 'Saving…' : 'Set'}
              </button>
            </div>
            <div className="w-muted" style={{ marginTop: 4 }}>
              Currently downloading to <code>{ytEffective || '…'}</code>
              {ytFolder.trim().length === 0 && ytEffective ? ' (default — nothing set)' : ''}
            </div>
            {ytNote && <div className="w-muted" style={{ marginTop: 2 }}>{ytNote}</div>}
          </fieldset>

          {msg && <div className="w-muted" style={{ marginTop: 4 }}>{msg}</div>}
          {err && <div className="w-err" style={{ marginTop: 4 }}>{err}</div>}
        </div>

        {/* Blocking confirmation for the destructive actions. Clicking the
            backdrop or Escape cancels; only the explicit button proceeds. */}
        {confirm && (
          <div className="w-overlay" onMouseDown={() => setConfirm(null)}>
            <div className="w-dialog w-raised" onMouseDown={e => e.stopPropagation()} style={{ width: 420, maxWidth: '90vw' }}>
              <div className="w-titlebar">
                <span className="w-app">{confirm.kind === 'clear' ? 'Clear folder tracks?' : 'Remove folder?'}</span>
                <span style={{ flex: 1 }} />
                <button className="w-btn" onClick={() => setConfirm(null)} style={{ minHeight: 16, padding: '0 7px' }}>✕</button>
              </div>
              <div className="w-dialog-body">
                <div style={{ marginBottom: 10 }}>
                  {confirm.kind === 'clear'
                    ? <>This removes <b>every track</b> under<br /><b>{confirm.folder.path}</b><br />({confirm.folder.trackCount} track{confirm.folder.trackCount === 1 ? '' : 's'}) from the library. The files stay on disk; the folder stays assigned and can be rescanned.</>
                    : <>This removes the folder<br /><b>{confirm.folder.path}</b><br />AND its {confirm.folder.trackCount} track{confirm.folder.trackCount === 1 ? '' : 's'} from the library. The files stay on disk.</>}
                </div>
                <div className="w-muted" style={{ marginBottom: 10 }}>
                  Tip: if you only want the folder out of search results, use its On/Off button instead — that hides without deleting.
                </div>
                <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end' }}>
                  <button className="w-btn" onClick={() => setConfirm(null)}>Cancel</button>
                  <button className="w-btn" onClick={runConfirm}>
                    {confirm.kind === 'clear' ? 'Yes, clear the tracks' : 'Yes, remove the folder'}
                  </button>
                </div>
              </div>
            </div>
          </div>
        )}

        {reloc && (
          <div className="w-overlay" onMouseDown={() => !relocBusy && setReloc(null)}>
            <div className="w-dialog w-raised" onMouseDown={e => e.stopPropagation()} style={{ width: 560, maxWidth: '92vw' }}>
              <div className="w-titlebar">
                <span className="w-app">Move music to a new location</span>
                <span style={{ flex: 1 }} />
                <button className="w-btn" disabled={relocBusy} onClick={() => setReloc(null)}
                  style={{ minHeight: 16, padding: '0 7px' }}>✕</button>
              </div>
              <div className="w-dialog-body">
                <div className="w-muted" style={{ marginBottom: 8 }}>
                  For when the files themselves moved — a new drive letter, a new machine, a new share.
                  Every track under the old location is pointed at the same place under the new one.
                  Tracks keep their identity, so playlists, the queue, history and analysis all carry over.
                  Nothing on disk is moved or changed.
                </div>

                <div className="w-formrow">
                  <label style={{ width: 70 }}>Old:</label>
                  <input type="text" value={reloc.from} style={{ flex: 1 }} spellCheck={false} disabled={relocBusy}
                    onChange={e => editReloc({ from: e.target.value })} />
                </div>
                <div className="w-muted" style={{ margin: '0 0 6px 74px' }}>
                  Shorten it to move several folders at once — e.g. <code>D:\Music</code> covers every folder under it.
                </div>
                <div className="w-formrow">
                  <label style={{ width: 70 }}>New:</label>
                  <input type="text" value={reloc.to} style={{ flex: 1 }} spellCheck={false} disabled={relocBusy}
                    placeholder="E:\Music"
                    onChange={e => editReloc({ to: e.target.value })}
                    onKeyDown={e => { if (e.key === 'Enter') checkRelocate() }} />
                  <button className="w-btn" disabled={relocBusy} onClick={() => setBrowsing('relocate')}>Browse…</button>
                </div>

                {relocPreview && (
                  <div className="w-sunken" style={{ padding: '6px 8px', margin: '8px 0', background: 'var(--w-field, #fff)' }}>
                    {relocPreview.error
                      ? <div className="w-err">{relocPreview.error}</div>
                      : <>
                          <div>
                            <b>{relocPreview.tracks}</b> track{relocPreview.tracks === 1 ? '' : 's'}
                            {relocPreview.folders > 0 && <> in <b>{relocPreview.folders}</b> folder{relocPreview.folders === 1 ? '' : 's'}</>}
                            {' '}will point at <code>{relocPreview.to}</code>.
                          </div>
                          {!relocPreview.targetExists && (
                            <div className="w-err">{relocPreview.to} can’t be reached from the server.</div>
                          )}
                          {relocPreview.sampled > 0 && (
                            <div className={relocPreview.sampleFound === 0 ? 'w-err'
                              : relocPreview.sampleFound < relocPreview.sampled ? undefined : 'w-muted'}>
                              Spot check: {relocPreview.sampleFound} of {relocPreview.sampled} files found at the new location
                              {relocPreview.sampleFound === 0 ? ' — is this the right folder?'
                                : relocPreview.sampleFound < relocPreview.sampled ? ' — some are missing; they stay unplayable until they turn up.'
                                : '.'}
                            </div>
                          )}
                          {relocPreview.duplicates > 0 && (
                            <div className="w-muted">
                              {relocPreview.duplicates} of them were already scanned at the new location; each pair is
                              merged into the original, so playlists keep working and nothing appears twice.
                            </div>
                          )}
                          {(relocPreview.youTubeFolder || relocPreview.youTubeEntries > 0) && (
                            <div className="w-muted">
                              The YouTube {relocPreview.youTubeFolder ? 'download folder and ' : ''}download records move with it.
                            </div>
                          )}
                        </>}
                  </div>
                )}

                <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end', marginTop: 8 }}>
                  <button className="w-btn" disabled={relocBusy} onClick={() => setReloc(null)}>Cancel</button>
                  <button className="w-btn" disabled={relocBusy || !reloc.from.trim() || !reloc.to.trim()}
                    onClick={checkRelocate}>{relocBusy && !relocReady ? 'Checking…' : 'Check'}</button>
                  <button className="w-btn w-primary" disabled={relocBusy || !relocReady} onClick={runRelocate}
                    title={relocReady ? undefined : 'Check the move first'}>
                    {relocBusy && relocReady ? 'Moving…' : 'Move'}
                  </button>
                </div>
              </div>
            </div>
          </div>
        )}

        {browsing && (
          <FolderBrowser
            onSelect={p => {
              if (browsing === 'youtube') setYtFolder(p)
              else if (browsing === 'relocate') editReloc({ to: p })
              else setNewPath(p)
              setBrowsing(null)
            }}
            onClose={() => setBrowsing(null)} />
        )}
      </div>
    </div>
  )
}
