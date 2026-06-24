# ─────────────────────────────────────────────────────────────────
#  Y2K Music Server — install.ps1
#
#  Installs (or upgrades) the service and the tray on a Windows
#  host. Must run elevated.
#
#  Layout produced:
#    C:\Program Files\Y2KMusicServer\        binaries
#    C:\ProgramData\Y2KMusicServer\          data + logs (preserved)
#
#  Idempotent: re-running this script against a host that already
#  has Y2K installed performs an in-place upgrade — service is
#  stopped, files are replaced, service is restarted. Vault data
#  under ProgramData is never touched.
# ─────────────────────────────────────────────────────────────────
[CmdletBinding()]
param(
    [string]$InstallRoot = "$env:ProgramFiles\Y2KMusicServer",
    [string]$DataRoot    = "$env:ProgramData\Y2KMusicServer",
    [string]$ServiceName = "Y2KMusicServer",
    [string]$DisplayName = "Y2K Music Server"
)

$ErrorActionPreference = 'Stop'

# ── Require elevation ──────────────────────────────────────────
$current = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($current)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "This installer must be run as Administrator." -ForegroundColor Red
    Write-Host "Right-click PowerShell and choose 'Run as administrator', then re-run."
    exit 1
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$payloadRoot = Split-Path -Parent $scriptDir   # zip layout: ./installer/ + ./server/ + ./tray/
$serverSrc = Join-Path $payloadRoot 'server'
$traySrc   = Join-Path $payloadRoot 'tray'

if (-not (Test-Path $serverSrc)) { throw "Missing payload: $serverSrc" }
if (-not (Test-Path $traySrc))   { throw "Missing payload: $traySrc" }

Write-Host "Y2K Music Server — installing" -ForegroundColor Cyan
Write-Host "  Install root: $InstallRoot"
Write-Host "  Data root:    $DataRoot"
Write-Host ""

# ── Stop running tray (any user) and service if present ───────
Write-Host "[1/8] Stopping running components…"
Get-Process -Name 'Y2KMusicServer.Tray' -ErrorAction SilentlyContinue |
    ForEach-Object { try { $_.Kill() } catch {} }

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') {
        try { Stop-Service -Name $ServiceName -Force -ErrorAction Stop }
        catch { Write-Warning "Could not stop service cleanly: $_" }
        Start-Sleep -Seconds 1
    }
}

# ── Copy binaries ──────────────────────────────────────────────
Write-Host "[2/8] Copying binaries to $InstallRoot…"
New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $InstallRoot 'server') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $InstallRoot 'tray')   -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $InstallRoot 'installer') -Force | Out-Null

Copy-Item -Path (Join-Path $serverSrc '*') -Destination (Join-Path $InstallRoot 'server') -Recurse -Force
Copy-Item -Path (Join-Path $traySrc '*')   -Destination (Join-Path $InstallRoot 'tray')   -Recurse -Force
Copy-Item -Path (Join-Path $scriptDir '*.ps1') -Destination (Join-Path $InstallRoot 'installer') -Force

# ── Create ProgramData layout ──────────────────────────────────
Write-Host "[3/8] Ensuring data folders…"
New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $DataRoot 'data')    -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $DataRoot 'logs')    -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $DataRoot '.server') -Force | Out-Null

# ── Register / update the service ──────────────────────────────
Write-Host "[4/8] Registering Windows Service…"
$serverExe = Join-Path $InstallRoot 'server\Y2KMusicServer.Server.exe'
if (-not (Test-Path $serverExe)) {
    throw "Server exe not found at $serverExe"
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    # Update binary path in case install root moved
    & sc.exe config $ServiceName binPath= "`"$serverExe`"" | Out-Null
} else {
    & sc.exe create $ServiceName binPath= "`"$serverExe`"" `
        DisplayName= "$DisplayName" start= auto obj= LocalSystem | Out-Null
    & sc.exe description $ServiceName "Y2K Music Server — radio automation / party DJ service." | Out-Null
}

# Recovery: restart on failure with 5s delay (3 attempts within the day)
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

# ── HKLM Run entry for the tray (launches for any signed-in user) ─
Write-Host "[5/8] Registering tray autostart…"
$trayExe = Join-Path $InstallRoot 'tray\Y2KMusicServer.Tray.exe'
$runKey  = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
New-ItemProperty -Path $runKey -Name 'Y2KMusicServerTray' `
    -Value "`"$trayExe`"" -PropertyType String -Force | Out-Null

# ── Add/Remove Programs entry ──────────────────────────────────
Write-Host "[6/8] Add/Remove Programs entry…"
$uninstKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Y2KMusicServer'
if (-not (Test-Path $uninstKey)) { New-Item -Path $uninstKey -Force | Out-Null }
$versionFile = Join-Path $InstallRoot 'server\Y2KMusicServer.Server.exe'
$verInfo = (Get-Item $versionFile).VersionInfo.FileVersion
Set-ItemProperty -Path $uninstKey -Name 'DisplayName'     -Value $DisplayName
Set-ItemProperty -Path $uninstKey -Name 'DisplayVersion'  -Value $verInfo
Set-ItemProperty -Path $uninstKey -Name 'Publisher'       -Value 'slq.dk'
Set-ItemProperty -Path $uninstKey -Name 'InstallLocation' -Value $InstallRoot
Set-ItemProperty -Path $uninstKey -Name 'UninstallString' -Value `
    "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$InstallRoot\installer\uninstall.ps1`""
Set-ItemProperty -Path $uninstKey -Name 'NoModify' -Value 1 -Type DWord
Set-ItemProperty -Path $uninstKey -Name 'NoRepair' -Value 1 -Type DWord

# ── Start the service ──────────────────────────────────────────
Write-Host "[7/8] Starting service…"
try {
    Start-Service -Name $ServiceName -ErrorAction Stop
} catch {
    Write-Warning "Service failed to start cleanly: $_"
    Write-Warning "Check logs under $DataRoot\logs"
    exit 2
}

# Probe /health to verify the service is responding
$ok = $false
for ($i = 0; $i -lt 20; $i++) {
    try {
        $resp = Invoke-WebRequest -Uri 'http://127.0.0.1:8765/health' `
            -UseBasicParsing -TimeoutSec 2 -ErrorAction Stop
        if ($resp.StatusCode -eq 200) { $ok = $true; break }
    } catch { Start-Sleep -Milliseconds 500 }
}
if (-not $ok) {
    Write-Warning "Service started but /health did not respond within 10s."
    Write-Warning "Check logs under $DataRoot\logs"
}

# ── Launch the tray for the current user ───────────────────────
Write-Host "[8/8] Launching tray…"
Start-Process -FilePath $trayExe -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Done. Open http://localhost:8765/admin in your browser." -ForegroundColor Green
