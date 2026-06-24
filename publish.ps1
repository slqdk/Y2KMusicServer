# ─────────────────────────────────────────────────────────────────
#  Y2K Music Server — publish.ps1
#
#  Produces a release zip: dist\Y2KMusicServer-<version>.zip
#
#  Steps:
#    1. Build the frontend (npm ci && npm run build).
#    2. Copy frontend dist into Server's wwwroot.
#    3. Publish the Server (self-contained win-x64).
#    4. Publish the Tray (self-contained win-x64).
#    5. Lay out the release tree: ./server, ./tray, ./installer.
#    6. Zip it.
#
#  Upload the resulting zip as a release asset on GitHub. The
#  tray's update checker will discover it on the next poll.
# ─────────────────────────────────────────────────────────────────
[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [switch]$SkipFrontend
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$dist     = Join-Path $root 'dist'
$stage    = Join-Path $dist  "Y2KMusicServer-$Version"
$staging  = Join-Path $stage 'staging'

if (Test-Path $dist) { Remove-Item -Path $dist -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# ── 1. Frontend ────────────────────────────────────────────────
$frontDir = Join-Path $root 'src\Y2KMusicServer.Frontend'
$serverWwwroot = Join-Path $root 'src\Y2KMusicServer.Server\wwwroot'

if (-not $SkipFrontend) {
    Write-Host "[1/6] Building frontend…" -ForegroundColor Cyan
    Push-Location $frontDir
    try {
        if (Test-Path 'node_modules') {
            npm ci
        } else {
            npm install
        }
        if ($LASTEXITCODE -ne 0) { throw "npm install failed" }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw "npm run build failed" }
    } finally { Pop-Location }

    Write-Host "[2/6] Copying frontend bundle into Server wwwroot…" -ForegroundColor Cyan
    if (Test-Path $serverWwwroot) {
        Get-ChildItem -Path $serverWwwroot -Recurse | Remove-Item -Force -Recurse
    } else {
        New-Item -ItemType Directory -Path $serverWwwroot -Force | Out-Null
    }
    Copy-Item -Path (Join-Path $frontDir 'dist\*') -Destination $serverWwwroot -Recurse -Force
} else {
    Write-Host "[1-2/6] Skipping frontend build (-SkipFrontend)" -ForegroundColor Yellow
}

# ── 3. Publish server ──────────────────────────────────────────
Write-Host "[3/6] Publishing Server (self-contained win-x64)…" -ForegroundColor Cyan
$serverOut = Join-Path $stage 'server'
dotnet publish (Join-Path $root 'src\Y2KMusicServer.Server\Y2KMusicServer.Server.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -p:PublishSingleFile=false `
    -o $serverOut
if ($LASTEXITCODE -ne 0) { throw "Server publish failed" }

# ── 4. Publish tray ────────────────────────────────────────────
Write-Host "[4/6] Publishing Tray (self-contained win-x64)…" -ForegroundColor Cyan
$trayOut = Join-Path $stage 'tray'
dotnet publish (Join-Path $root 'src\Y2KMusicServer.Tray\Y2KMusicServer.Tray.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -p:PublishSingleFile=false `
    -o $trayOut
if ($LASTEXITCODE -ne 0) { throw "Tray publish failed" }

# ── 5. Copy installer scripts ──────────────────────────────────
Write-Host "[5/6] Copying installer scripts…" -ForegroundColor Cyan
$instOut = Join-Path $stage 'installer'
New-Item -ItemType Directory -Path $instOut -Force | Out-Null
Copy-Item -Path (Join-Path $root 'installer\*.ps1') -Destination $instOut -Force

# ── 6. Zip ─────────────────────────────────────────────────────
Write-Host "[6/6] Zipping…" -ForegroundColor Cyan
$zipPath = Join-Path $dist "Y2KMusicServer-$Version.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -Force

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Zip:  $zipPath"
Write-Host "  Size: $((Get-Item $zipPath).Length / 1MB) MB" -NoNewline
Write-Host ""
Write-Host ""
Write-Host "Next steps:"
Write-Host "  - Create a GitHub Release with tag v$Version"
Write-Host "  - Upload $zipPath as the release asset"
Write-Host "  - The tray's update checker will discover it on the next poll"
