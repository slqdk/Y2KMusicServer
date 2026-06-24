<#
.SYNOPSIS
    Starts the Y2K Music Server development stack.

.DESCRIPTION
    Launches the ASP.NET Core service (console, Development environment) and the
    Vite frontend dev server, each in its own window. This is the script-driven
    equivalent of setting "Multiple Startup Projects" in Visual Studio.

      • Server   : dotnet run from src\Y2KMusicServer.Server
                   DOTNET_ENVIRONMENT=Development  ->  Kestrel on http://localhost:8765
                   dev data + db live in  src\Y2KMusicServer.Server\.dev-data
      • Frontend : npm run dev  ->  Vite on http://localhost:5173
                   proxies /api, /hub, /stream to :8765 (see vite.config.ts)
      • Tray     : optional (-Tray), WPF tray app (net8.0-windows)

    First run restores NuGet packages (incl. NAudio.Lame) and, if needed, npm
    packages — give it a moment.

.PARAMETER Clean
    Delete the dev data folder (.dev-data) before starting, for a fresh reseed
    of the SQLite database and a clean log set.

.PARAMETER Tray
    Also start the WPF tray app.

.PARAMETER NoFrontend
    Skip the Vite dev server (back-end / API work only).

.PARAMETER NoServer
    Skip the service (front-end-only; assumes a service is already running).

.PARAMETER Force
    Don't prompt for confirmation on -Clean.

.EXAMPLE
    .\dev.ps1
    Start server + frontend.

.EXAMPLE
    .\dev.ps1 -Clean -Tray
    Wipe dev data, then start server + frontend + tray.
#>

[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$Tray,
    [switch]$NoFrontend,
    [switch]$NoServer,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$root        = $PSScriptRoot
$serverDir   = Join-Path $root 'src\Y2KMusicServer.Server'
$frontendDir = Join-Path $root 'src\Y2KMusicServer.Frontend'
$trayDir     = Join-Path $root 'src\Y2KMusicServer.Tray'
$devData     = Join-Path $serverDir '.dev-data'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Warn2($msg) { Write-Host "    ! $msg" -ForegroundColor Yellow }

# Prefer PowerShell 7 (pwsh) for the child windows; fall back to Windows PowerShell.
$psExe = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }

# ── Preflight ────────────────────────────────────────────────────────────────
Write-Step "Checking prerequisites"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK ('dotnet') was not found on PATH. Install the .NET 8 SDK."
}
$dotnetVer = (dotnet --version).Trim()
Write-Host "    .NET SDK $dotnetVer"
if (($dotnetVer.Split('.')[0] -as [int]) -lt 8) {
    Write-Warn2 "This project targets .NET 8; SDK $dotnetVer may not build it."
}

if (-not $NoFrontend) {
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
        throw "Node.js ('node') was not found on PATH. Install Node 20+ (or run with -NoFrontend)."
    }
    $nodeVer = (node --version).TrimStart('v').Trim()
    Write-Host "    Node $nodeVer"
    if (($nodeVer.Split('.')[0] -as [int]) -lt 20) {
        Write-Warn2 "Vite 5 expects Node 20+; Node $nodeVer may warn or fail."
    }
}

# ── Clean dev data ───────────────────────────────────────────────────────────
if ($Clean) {
    if (Test-Path $devData) {
        $go = $Force
        if (-not $go) {
            $ans = Read-Host "Delete dev data at '$devData'? This drops the dev DB. (y/N)"
            $go = ($ans -eq 'y' -or $ans -eq 'Y')
        }
        if ($go) {
            Write-Step "Removing $devData"
            Remove-Item -Recurse -Force $devData
            Write-Host "    Gone. The service will recreate the schema and reseed on next start."
        } else {
            Write-Warn2 "Skipped clean."
        }
    } else {
        Write-Warn2 "-Clean requested but no .dev-data folder exists yet (nothing to remove)."
    }
}

# ── Frontend deps ────────────────────────────────────────────────────────────
if (-not $NoFrontend) {
    if (-not (Test-Path (Join-Path $frontendDir 'node_modules'))) {
        Write-Step "Installing frontend dependencies (first run)"
        Push-Location $frontendDir
        try {
            npm install
            if ($LASTEXITCODE -ne 0) { throw "npm install failed (exit $LASTEXITCODE)." }
        } finally { Pop-Location }
    }
}

# ── Launch ───────────────────────────────────────────────────────────────────
# Each long-running process opens in its own window so its logs stay separate.
# Close a window (or Ctrl+C inside it) to stop that process.

if (-not $NoServer) {
    Write-Step "Starting service  ->  http://localhost:8765  (window: 'Y2K Server')"
    $serverCmd = "`$host.UI.RawUI.WindowTitle='Y2K Server :8765'; " +
                 "Set-Location '$serverDir'; " +
                 "`$env:DOTNET_ENVIRONMENT='Development'; " +
                 "dotnet run"
    Start-Process -FilePath $psExe -ArgumentList '-NoExit','-Command',$serverCmd | Out-Null
}

if (-not $NoFrontend) {
    Write-Step "Starting frontend ->  http://localhost:5173  (window: 'Y2K Frontend')"
    $frontendCmd = "`$host.UI.RawUI.WindowTitle='Y2K Frontend :5173'; " +
                   "Set-Location '$frontendDir'; " +
                   "npm run dev"
    Start-Process -FilePath $psExe -ArgumentList '-NoExit','-Command',$frontendCmd | Out-Null
}

if ($Tray) {
    Write-Step "Starting tray     (window: 'Y2K Tray')"
    $trayCmd = "`$host.UI.RawUI.WindowTitle='Y2K Tray'; " +
               "Set-Location '$trayDir'; " +
               "dotnet run"
    Start-Process -FilePath $psExe -ArgumentList '-NoExit','-Command',$trayCmd | Out-Null
}

# ── Summary ──────────────────────────────────────────────────────────────────
Write-Host ""
Write-Step "Dev stack launched"
if (-not $NoFrontend) { Write-Host "    Open this  ->  http://localhost:5173   (Vite; proxies API/hub/stream to :8765)" -ForegroundColor Green }
if (-not $NoServer)   { Write-Host "    Service / admin API:   http://localhost:8765" }
if (-not $NoServer)   { Write-Host "    Health probe:          http://localhost:8765/health" }
Write-Host ""
Write-Host "    Stream (after enabling):" -ForegroundColor DarkGray
Write-Host "      POST http://localhost:8765/api/admin/stream/enable?on=true" -ForegroundColor DarkGray
Write-Host "      WAV  http://localhost:8765/stream            MP3  http://localhost:8765/stream?format=mp3" -ForegroundColor DarkGray
Write-Host ""
Write-Host "    Each process runs in its own window; close a window to stop it." -ForegroundColor DarkGray
Write-Host "    Clean reseed:  .\dev.ps1 -Clean" -ForegroundColor DarkGray
