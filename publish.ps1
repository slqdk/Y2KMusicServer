# ─────────────────────────────────────────────────────────────────
#  Y2K Music Server — publish.ps1
#
#  Produces a release zip: dist\Y2KMusicServer-<version>.zip
#
#  Steps:
#    1. Build the frontend (npm ci && npm run build).
#    2. Copy frontend dist into Server's wwwroot.
#    3. Publish the Server (framework-dependent win-x64).
#    4. Publish the Tray (framework-dependent win-x64).
#    5. Lay out the release tree: ./server, ./tray, ./installer.
#    6. Zip it.
#
#  This is a framework-dependent build: the .NET 8 runtime is NOT
#  bundled, which keeps the zip small (~15 MB instead of ~115 MB) so
#  the tray's click-to-update downloads in seconds. Target hosts must
#  have the .NET 8 Desktop Runtime + ASP.NET Core 8 Runtime installed
#  once; install.ps1 checks for them and aborts loudly if missing.
#
#  Pass -Release to also tag (vX.Y.Z), push, and publish a GitHub
#  Release with the zip attached in one step. Requires the `gh` CLI
#  on PATH and authenticated (`gh auth login`), and a CLEAN working
#  tree — the release must match what's on GitHub at the tagged
#  commit. Add -Prerelease to mark it a pre-release (note: the tray
#  checks releases/latest, which ignores pre-releases unless the
#  latest IS one). Without -Release the zip is built and you upload
#  it to a GitHub Release yourself.
# ─────────────────────────────────────────────────────────────────
[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [switch]$SkipFrontend,
    [switch]$Release,
    [switch]$Prerelease
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# ── gh helper + -Release pre-flight ────────────────────────────
# Done UP FRONT so a misconfigured release fails in seconds rather
# than minutes after the build. Nothing here mutates the repo.
function Test-Tool {
    param([string]$Name, [string]$VersionArg = "--version")
    try {
        $output = & $Name $VersionArg 2>&1
        Write-Host "  [ok] $Name $($output | Select-Object -First 1)" -ForegroundColor DarkGray
    } catch {
        throw "$Name not found on PATH. Install it before running this script."
    }
}

if ($Release) {
    Write-Host "Pre-flight checks for -Release..." -ForegroundColor Cyan

    # gh CLI must be installed and authenticated.
    Test-Tool "gh"
    try {
        & gh auth status 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "not authed" }
    } catch {
        throw "gh CLI is not authenticated. Run: gh auth login"
    }

    # Working tree must be clean. A release that doesn't match HEAD
    # is an attractive nuisance: install the zip, look at the source
    # assuming it matches, get confused. Refuse loudly.
    Push-Location $root
    try {
        $dirty = & git status --porcelain
    } finally {
        Pop-Location
    }
    if ($dirty) {
        Write-Host ""
        Write-Host "Refusing to publish a release: working tree is dirty." -ForegroundColor Red
        Write-Host "Uncommitted changes:" -ForegroundColor Red
        Write-Host $dirty -ForegroundColor Yellow
        Write-Host ""
        Write-Host "Commit, stash, or revert them first. The release zip"
        Write-Host "must match what's on GitHub at the tagged commit."
        throw "Dirty working tree blocks -Release."
    }

    # Tag must not already exist locally or on origin.
    Push-Location $root
    try {
        $existingTag = & git tag --list "v$Version"
        if ($existingTag) {
            throw "Tag v$Version already exists locally. Delete it (git tag -d v$Version) or pick a new version."
        }
        $remoteTag = & git ls-remote --tags origin "refs/tags/v$Version" 2>$null
        if ($remoteTag) {
            throw "Tag v$Version already exists on origin. Pick a new version."
        }
    } finally {
        Pop-Location
    }

    Write-Host "  [ok] gh authed, working tree clean, tag v$Version free" -ForegroundColor DarkGray
}

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
Write-Host "[3/6] Publishing Server (framework-dependent win-x64)…" -ForegroundColor Cyan
$serverOut = Join-Path $stage 'server'
dotnet publish (Join-Path $root 'src\Y2KMusicServer.Server\Y2KMusicServer.Server.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:Version=$Version `
    -p:PublishSingleFile=false `
    -o $serverOut
if ($LASTEXITCODE -ne 0) { throw "Server publish failed" }

# ── 4. Publish tray ────────────────────────────────────────────
Write-Host "[4/6] Publishing Tray (framework-dependent win-x64)…" -ForegroundColor Cyan
$trayOut = Join-Path $stage 'tray'
dotnet publish (Join-Path $root 'src\Y2KMusicServer.Tray\Y2KMusicServer.Tray.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained false `
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

# ── 7. GitHub Release (only with -Release) ─────────────────────
# Tag + push + create release with the zip attached. All checks were
# done up front; if we got here, gh is installed, authed, the tree is
# clean, and the tag doesn't exist yet. Title is "Release X.Y.Z",
# body empty — edit on github.com afterwards; the tray's update
# window shows whatever's there.
if ($Release) {
    Write-Host ""
    Write-Host "-- Publishing GitHub Release v$Version --" -ForegroundColor Cyan

    Push-Location $root
    try {
        # Push current branch first. If local HEAD is ahead of origin
        # (a freshly-committed version bump, say), the tag we push next
        # would point at a commit origin doesn't have, and gh release
        # create would fail resolving it. `git push` with no args
        # pushes the current branch to its upstream; safe — the tree is
        # already verified clean. A no-op if nothing's pending.
        Write-Host "Pushing current branch to origin…" -ForegroundColor White
        & git push
        if ($LASTEXITCODE -ne 0) { throw "git push (branch) failed" }

        Write-Host "Creating annotated git tag v$Version at HEAD…" -ForegroundColor White
        & git tag -a "v$Version" -m "Release $Version"
        if ($LASTEXITCODE -ne 0) { throw "git tag failed" }

        Write-Host "Pushing tag to origin…" -ForegroundColor White
        & git push origin "v$Version"
        if ($LASTEXITCODE -ne 0) { throw "git push (tag) failed" }
    } finally {
        Pop-Location
    }

    # Empty notes via a temp file, NOT --notes "" — PowerShell strips
    # the empty string and gh then misreads the next flag as the notes
    # value. --notes-file with an empty file dodges that entirely.
    $notesFile = New-TemporaryFile
    try {
        Set-Content -Path $notesFile.FullName -Value "" -Encoding UTF8 -NoNewline

        $ghArgs = @(
            "release", "create", "v$Version",
            $zipPath,
            "--title", "Release $Version",
            "--notes-file", $notesFile.FullName
        )
        if ($Prerelease) { $ghArgs += "--prerelease" }

        Write-Host "Creating GitHub Release…" -ForegroundColor White
        & gh @ghArgs
        if ($LASTEXITCODE -ne 0) {
            # Tag is already pushed; best-effort rollback so the next
            # attempt doesn't trip over the existing tag.
            Write-Host "Release creation failed. Rolling back the tag…" -ForegroundColor Red
            try { & git tag -d "v$Version" 2>&1 | Out-Null } catch {}
            try { & git push origin ":refs/tags/v$Version" 2>&1 | Out-Null } catch {}
            throw "gh release create failed."
        }
    } finally {
        if (Test-Path $notesFile.FullName) {
            Remove-Item $notesFile.FullName -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host ""
    Write-Host "Release v$Version published." -ForegroundColor Green
    Write-Host "  https://github.com/slqdk/Y2kMusicServer/releases/tag/v$Version" -ForegroundColor Cyan
}

# ── Summary ────────────────────────────────────────────────────
Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Zip:  $zipPath"
Write-Host "  Size: $((Get-Item $zipPath).Length / 1MB) MB" -NoNewline
Write-Host ""
Write-Host ""
if ($Release) {
    Write-Host "Released: v$Version"
    Write-Host "  https://github.com/slqdk/Y2kMusicServer/releases/tag/v$Version"
    Write-Host "  The tray's update checker will discover it on the next poll."
    Write-Host "  Add release notes by editing the release on github.com."
} else {
    Write-Host "Next steps (manual release):"
    Write-Host "  - Create a GitHub Release with tag v$Version"
    Write-Host "  - Upload $zipPath as the release asset"
    Write-Host "  - The tray's update checker will discover it on the next poll"
    Write-Host "  - Or re-run with -Release to tag + upload + publish in one step"
}
