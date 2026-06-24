# Y2K Music Server

A self-hosted Windows radio-automation / party DJ service.
Plays MP3 / WAV / FLAC from folders on disk, crossfades between
tracks with beat-aware mix points, auto-fills its playlist from
time-scheduled categories, serves a request page to listeners on
the LAN, and broadcasts a live MP3 stream.

The server runs as a Windows Service. Operators run the admin
page in a browser. Listeners run the request page in a browser
or any MP3 stream player.

---

## Status

Active rewrite — porting a working Visual Studio 2017 / WinForms
implementation to .NET 8 / ASP.NET Core 8 / VS2022 with the
NoteControl-style "service + tray + web UI" shape. See
`CHANGELOG.md` (when it lands) for ship history and the
[GitHub Releases page](https://github.com/slqdk/Y2kMusicServer/releases)
for downloads.

## Architecture

```
┌────────────────────────────────────────────────────────────┐
│ Windows host                                               │
│                                                            │
│  ┌────────────────┐         ┌──────────────────────────┐   │
│  │ Tray (WPF)     │  HTTP   │ Server                   │   │
│  │ Open admin     │ ──────► │ ASP.NET Core 8 +         │   │
│  │ Update notify  │         │ EF Core + SQLite +       │   │
│  │ Start / stop   │         │ SignalR + NAudio engine  │   │
│  └────────────────┘         │ runs as Windows Service  │   │
│                             └────────────┬─────────────┘   │
│                                          ▼                 │
│                       C:\ProgramData\Y2KMusicServer\       │
└────────────────────────────────────────────────────────────┘
            ▲                            ▲
            │ Browser (LAN)              │ /stream (MP3)
   ┌────────┴────────────┐      ┌────────┴──────────┐
   │  /        listener   │     │ Stream player      │
   │  /admin   operator   │     │ (VLC, foobar, …)   │
   └──────────────────────┘     └────────────────────┘
```

| Layer | Technology |
|---|---|
| Server | ASP.NET Core 8 (Kestrel), runs as a Windows Service |
| Audio engine | NAudio 2.2 (NAudio + BunLabs.NAudio.Flac) |
| Database | SQLite via EF Core 8 |
| Logs | Serilog → daily files |
| Live updates | SignalR (server → admin push) |
| Tray | WPF (net8.0-windows) + H.NotifyIcon |
| Frontend | React + Vite + TypeScript |
| Updates | GitHub Releases, polled by the tray |
| Installer | PowerShell |

## Install (binary)

1. Download `Y2KMusicServer-<version>.zip` from
   [Releases](https://github.com/slqdk/Y2kMusicServer/releases/latest).
2. Extract anywhere.
3. Run an **elevated PowerShell** and execute:

   ```
   cd path\to\extracted\Y2KMusicServer-<version>
   .\installer\install.ps1
   ```
4. The installer:
   - Copies binaries to `C:\Program Files\Y2KMusicServer\`
   - Registers `Y2KMusicServer` as a Windows service (auto-start)
   - Adds the tray to HKLM Run (launches at login for any user)
   - Adds an Add/Remove Programs entry
   - Probes `http://127.0.0.1:8765/health` to verify
5. Open <http://localhost:8765/admin> in your browser.

### Update

Once installed, the tray polls GitHub Releases once per day.
Newer release → menu shows "Update available: X.Y.Z" → click it
to download and run the new installer (with UAC prompt). About
30 seconds end-to-end, no reboot.

### Uninstall

- Add/Remove Programs → Y2K Music Server → Uninstall, **OR**
- Elevated PowerShell:
  `& "C:\Program Files\Y2KMusicServer\installer\uninstall.ps1"`

Data under `C:\ProgramData\Y2KMusicServer\` is preserved unless
you pass `-RemoveData`.

## Build from source

Requirements:

- Windows 10 / 11
- Visual Studio 2022 (workloads: *.NET desktop development*,
  *ASP.NET and web development*)
- .NET 8 SDK
- Node.js 20+ and npm

```
git clone https://github.com/slqdk/Y2kMusicServer.git
cd Y2kMusicServer
```

Open `Y2KMusicServer.sln` in VS2022. The four projects:

| Project | What it does |
|---|---|
| `Y2KMusicServer.Server` | The ASP.NET Core 8 service. F5 to run as a console for local dev. |
| `Y2KMusicServer.Tray` | The WPF tray. Set as startup project alongside Server for full local testing. |
| `Y2KMusicServer.Shared` | DTOs shared between Server and Tray. |
| `Y2KMusicServer.Tests` | xUnit tests. |

The frontend lives in `src/Y2KMusicServer.Frontend/` as a Vite
project. `npm run dev` runs the Vite dev server (proxied to the
Kestrel port); `publish.ps1` builds it into the Server's
`wwwroot/` for production.

### Produce a release zip

```
.\publish.ps1 -Version 0.1.0
```

Output: `dist\Y2KMusicServer-0.1.0.zip`. Upload as a GitHub
Release asset and the in-app updater will pick it up.

## Where things live on disk

| Path | Contents |
|---|---|
| `C:\Program Files\Y2KMusicServer\` | Binaries + installer scripts |
| `C:\ProgramData\Y2KMusicServer\data\` | SQLite database, scanned-folder config |
| `C:\ProgramData\Y2KMusicServer\logs\` | Daily Serilog files |
| `C:\ProgramData\Y2KMusicServer\.server\` | Service state (locks, etc.) |

## License

MIT — see [`LICENSE`](LICENSE).
