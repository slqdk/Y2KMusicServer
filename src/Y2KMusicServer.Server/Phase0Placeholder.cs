namespace Y2KMusicServer.Server;

/// <summary>
/// Phase 0 only. Produces a placeholder landing-page HTML used
/// by the SPA fallback in <c>Program.cs</c> when no built
/// frontend exists in <c>wwwroot/</c>. Once a real frontend
/// build lands in wwwroot, the fallback serves that instead and
/// this helper becomes unreachable. Delete the call site and
/// this file together when removing the placeholder.
/// </summary>
internal static class Phase0Placeholder
{
    public static string Html(string requestedPath) => $@"<!doctype html>
<html lang=""en"">
<head>
  <meta charset=""utf-8"" />
  <meta name=""viewport"" content=""width=device-width,initial-scale=1"" />
  <title>Y2K Music Server — Phase 0</title>
  <style>
    body {{ font-family: ui-sans-serif, system-ui, sans-serif;
           background: #0a0a0f; color: #eee;
           margin: 0; padding: 2rem; max-width: 760px; }}
    h1 {{ color: #a8c1ff; margin: 0 0 0.4rem; }}
    h2 {{ color: #c4b5fd; margin: 1.6rem 0 0.6rem; font-size: 1.1rem; }}
    p  {{ margin: 0.4rem 0; color: #aaa; }}
    pre {{ background: #16161e; padding: 0.8rem 1rem;
          border-radius: 4px; overflow-x: auto;
          color: #ddd; font-size: 0.9rem; }}
    .ok {{ color: #6ee7b7; }}
    .pending {{ color: #fde68a; }}
    a {{ color: #93c5fd; }}
    code {{ background: #1a1a22; padding: 0.1em 0.35em;
            border-radius: 3px; font-size: 0.95em; }}
  </style>
</head>
<body>
  <h1>Y2K Music Server</h1>
  <p>Phase 0 scaffold — no built frontend yet. You requested
     <code>{System.Net.WebUtility.HtmlEncode(requestedPath)}</code>. This page
     evaporates automatically once <code>wwwroot/index.html</code>
     exists (which happens when <code>publish.ps1</code> builds the
     real frontend, scheduled for Phase 4).</p>

  <h2>What's wired up</h2>
  <p class=""ok"">✓ Kestrel running</p>
  <p class=""ok"">✓ Serilog → console + daily file</p>
  <p class=""ok"">✓ SignalR <code>/hub/playback</code> with 5s heartbeat</p>
  <p class=""ok"">✓ Admin status <code>/api/admin/service/status</code></p>
  <p class=""ok"">✓ Liveness probe <code>/health</code></p>
  <p class=""ok"">✓ GitHub Releases update checker</p>

  <h2>What's not here yet</h2>
  <p class=""pending"">⏳ Audio engine, decks, crossfade — Phase 2</p>
  <p class=""pending"">⏳ SQLite / EF Core — Phase 1</p>
  <p class=""pending"">⏳ Library scanning — Phase 1</p>
  <p class=""pending"">⏳ Auto DJ, streaming — Phase 3</p>
  <p class=""pending"">⏳ Win2000-skinned admin UI — Phase 4</p>
  <p class=""pending"">⏳ Listener page — Phase 4</p>

  <h2>SignalR heartbeat</h2>
  <p>The box below should tick every 5 seconds. If it doesn't,
     the SignalR pipeline isn't connected.</p>
  <pre id=""tick"">connecting…</pre>

  <p style=""margin-top: 2rem;"">
    <a href=""/api/admin/service/status"">Service status JSON</a>
  </p>

  <script src=""https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/8.0.0/signalr.min.js""></script>
  <script>
    (async () => {{
      const tick = document.getElementById('tick');
      try {{
        const conn = new signalR.HubConnectionBuilder()
          .withUrl('/hub/playback')
          .withAutomaticReconnect()
          .build();
        conn.on('hello', m => {{ tick.textContent = 'hello → ' + JSON.stringify(m, null, 2); }});
        conn.on('tick',  m => {{ tick.textContent = 'tick #' + m.n + ' @ ' + m.serverTimeUtc; }});
        await conn.start();
      }} catch (e) {{
        tick.textContent = 'SignalR connect failed: ' + e;
      }}
    }})();
  </script>
</body>
</html>";
}
