# ─────────────────────────────────────────────────────────────────
#  Y2K Music Server — uninstall.ps1
#
#  Removes the service, the tray autostart, the binaries, and the
#  Add/Remove Programs entry. Data under
#  C:\ProgramData\Y2KMusicServer is preserved unless -RemoveData
#  is passed (with confirmation).
# ─────────────────────────────────────────────────────────────────
[CmdletBinding()]
param(
    [switch]$RemoveData,
    [string]$InstallRoot = "$env:ProgramFiles\Y2KMusicServer",
    [string]$DataRoot    = "$env:ProgramData\Y2KMusicServer",
    [string]$ServiceName = "Y2KMusicServer"
)

$ErrorActionPreference = 'Continue'

# ── Require elevation ──────────────────────────────────────────
$current = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($current)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "This uninstaller must be run as Administrator." -ForegroundColor Red
    exit 1
}

Write-Host "Y2K Music Server — uninstalling" -ForegroundColor Cyan

# ── Stop and remove service ───────────────────────────────────
Write-Host "[1/5] Stopping and removing service…"
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    try { Stop-Service -Name $ServiceName -Force -ErrorAction Stop } catch {}
    Start-Sleep -Seconds 1
    & sc.exe delete $ServiceName | Out-Null
}

# ── Kill tray ──────────────────────────────────────────────────
Write-Host "[2/5] Stopping tray…"
Get-Process -Name 'Y2KMusicServer.Tray' -ErrorAction SilentlyContinue |
    ForEach-Object { try { $_.Kill() } catch {} }

# ── Remove HKLM Run entry ──────────────────────────────────────
Write-Host "[3/5] Removing tray autostart…"
$runKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -Path $runKey -Name 'Y2KMusicServerTray' -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $runKey -Name 'Y2KMusicServerTray' -ErrorAction SilentlyContinue
}

# ── Remove Add/Remove Programs entry ───────────────────────────
$uninstKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Y2KMusicServer'
if (Test-Path $uninstKey) { Remove-Item -Path $uninstKey -Recurse -Force }

# ── Remove binaries ────────────────────────────────────────────
Write-Host "[4/5] Removing binaries from $InstallRoot…"
if (Test-Path $InstallRoot) {
    # Retry loop: files may still be locked briefly after service stop
    for ($i = 0; $i -lt 5; $i++) {
        try {
            Remove-Item -Path $InstallRoot -Recurse -Force -ErrorAction Stop
            break
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    if (Test-Path $InstallRoot) {
        Write-Warning "Could not fully remove $InstallRoot. Remove manually after reboot."
    }
}

# ── Optionally remove data ─────────────────────────────────────
Write-Host "[5/5] Data folder…"
if ($RemoveData) {
    Write-Host "  -RemoveData specified." -ForegroundColor Yellow
    $confirm = Read-Host "Type DELETE to permanently remove $DataRoot"
    if ($confirm -ceq 'DELETE') {
        if (Test-Path $DataRoot) {
            Remove-Item -Path $DataRoot -Recurse -Force -ErrorAction Continue
        }
        Write-Host "  Data removed."
    } else {
        Write-Host "  Skipped (no match)."
    }
} else {
    Write-Host "  Preserved at $DataRoot (pass -RemoveData to delete)."
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
