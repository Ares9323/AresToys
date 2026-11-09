# Local Velopack packaging dry-run — mirrors what .github/workflows/release.yml does in CI,
# minus the "upload to GitHub" final step. Produces the same artifacts (Setup.exe + Portable.zip
# + .nupkg + RELEASES) under Releases/ so you can install/run them locally before cutting a real
# tag.
#
# Usage (from the repo root):
#   pwsh tools/pack-local.ps1                     # auto-pick last version, overwrite
#   pwsh tools/pack-local.ps1 -Version 0.2.0      # explicit version
#   pwsh tools/pack-local.ps1 -SkipBuild          # reuse previous bin/Release output
#   pwsh tools/pack-local.ps1 -KeepReleases       # don't wipe existing artifacts (preserves delta baseline)
#
# After it finishes:
#   - Releases\AresToys-<channel>-Setup.exe       → run to install into %LocalAppData%\AresToys
#   - Releases\AresToys-<channel>-Portable.zip    → unzip anywhere and run AresToys.exe directly
#   - Releases\AresToys-<ver>-full.nupkg          → Velopack catalog file (the in-app updater fetches it)
#
# The script exits on the first failed step so a broken publish doesn't leave you with a stale
# Releases\ that still references the previous good build.

[CmdletBinding()]
param(
    # Optional. When omitted, the script picks the highest existing version in Releases/ and
    # re-packs THAT (overwriting the previous artifacts). First-ever run with no prior pack
    # falls back to '0.1.0-local'.
    [string]$Version,

    [string]$Channel = 'win',

    [string]$Runtime = 'win-x64',

    # Reuse an already-built solution. Saves ~20-30s on iterative pack tests when only the
    # Velopack metadata changed.
    [switch]$SkipBuild,

    # Skip the publish (use the existing publish/ folder). Use only when you've manually
    # tweaked publish/ — normally publish should be re-run after every code change.
    [switch]$SkipPublish,

    # Don't wipe existing Releases\ artifacts before packing. By default the script clears
    # them so the same version can be re-packed without vpk's "release equal-or-greater
    # exists" check tripping. Pass this flag if you're testing the delta-pack flow and need
    # the previous nupkg as a baseline.
    [switch]$KeepReleases
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

# Resolve the target version if the caller didn't supply one. Source of truth: the csproj's
# <Version> element with the '-dev' suffix stripped — that suffix is the IDE-build marker
# (Settings → About reads it back from AssemblyInformationalVersion when running from
# bin/Debug). A pack is by definition a release-grade artifact, so we drop '-dev' and ship
# the clean SemVer; the same convention CI's release.yml follows when it gets a workflow
# input like '0.1.0'.
if (-not $Version) {
    $csprojPath = Join-Path $repoRoot 'src/AresToys.App/AresToys.App.csproj'
    if (Test-Path $csprojPath) {
        [xml]$csprojXml = Get-Content $csprojPath
        $projVersion = $csprojXml.Project.PropertyGroup |
            ForEach-Object { $_.Version } |
            Where-Object { $_ } |
            Select-Object -First 1
        if ($projVersion) {
            $Version = $projVersion -replace '-dev$', ''
            Write-Host "==> No -Version supplied; using csproj <Version> minus '-dev' suffix: $Version" -ForegroundColor Yellow
        }
    }
    if (-not $Version) {
        # Last-resort fallback if the csproj is missing or unparseable — keep this clean (no
        # "-local") so a stray pack doesn't carry a dev-only suffix into the installer name.
        $Version = '0.1.0'
        Write-Host "==> No -Version supplied and csproj <Version> not found; defaulting to $Version" -ForegroundColor Yellow
    }
}

Write-Host "==> Restoring .NET tools (vpk)" -ForegroundColor Cyan
dotnet tool restore | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed" }

if (-not $SkipBuild) {
    Write-Host "==> Restoring solution" -ForegroundColor Cyan
    dotnet restore AresToys.sln | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

    Write-Host "==> Building (Release)" -ForegroundColor Cyan
    dotnet build AresToys.sln --configuration Release --no-restore | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
}
else {
    Write-Host "==> Skipping build (-SkipBuild)" -ForegroundColor Yellow
}

if (-not $SkipPublish) {
    Write-Host "==> Publishing AresToys.App ($Runtime, self-contained)" -ForegroundColor Cyan
    # Self-contained ships the .NET runtime alongside AresToys so end users don't need the SDK
    # installed. -p:Version is what Settings → About reads via Assembly.GetName().Version.
    # Velopack uses --packVersion below for its own metadata; we keep them in lockstep.
    dotnet publish src/AresToys.App/AresToys.App.csproj `
        --configuration Release `
        --runtime $Runtime `
        --self-contained true `
        -p:Version=$Version `
        -p:PublishSingleFile=false `
        -p:PublishReadyToRun=false `
        -o publish | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}
else {
    Write-Host "==> Skipping publish (-SkipPublish)" -ForegroundColor Yellow
}

# Clean previous artifacts for this channel so vpk doesn't refuse with "release equal or
# greater exists". Wipes nupkgs + catalog files only — the user's other channels (e.g. a
# kept-around `local` channel) are untouched. Pass -KeepReleases to skip and preserve a
# delta-pack baseline.
if (-not $KeepReleases) {
    $patterns = @(
        "AresToys-*.nupkg"                # full + delta payloads (all channels share the dir)
        "AresToys-$Channel-*"             # Setup.exe + Portable.zip + per-channel installer assets
        "RELEASES*"                     # legacy + per-channel catalog files
        "releases*.json"                # JSON catalog
        "assets*.json"                  # JSON asset manifest
    )
    $blockers = @()
    foreach ($p in $patterns) {
        $blockers += Get-ChildItem "Releases" -Filter $p -File -ErrorAction SilentlyContinue
    }
    if ($blockers) {
        Write-Host "==> Cleaning previous artifacts for channel '$Channel':" -ForegroundColor Yellow
        $blockers | ForEach-Object { Write-Host "    - $($_.Name)" }
        $blockers | Remove-Item -Force
    }
}
else {
    Write-Host "==> Skipping Releases\ cleanup (-KeepReleases)" -ForegroundColor Yellow
}

Write-Host "==> Packing with Velopack ($Version, channel=$Channel)" -ForegroundColor Cyan
# vpk pack output layout:
#   Releases\AresToys-<channel>-Setup.exe       (installer)
#   Releases\AresToys-<channel>-Portable.zip    (unzip-anywhere build)
#   Releases\AresToys-<ver>-full.nupkg          (full update payload)
#   Releases\AresToys-<ver>-delta.nupkg         (delta vs. previous, if a prior pack exists)
#   Releases\RELEASES                         (Velopack catalog the in-app updater fetches)
dotnet vpk pack `
    --packId AresToys `
    --packVersion $Version `
    --packDir publish `
    --mainExe AresToys.exe `
    --packTitle "AresToys" `
    --packAuthors "AresToys contributors" `
    --channel $Channel | Out-Host
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

Write-Host ""
Write-Host "==> Done. Artifacts under Releases\:" -ForegroundColor Green
Get-ChildItem Releases -File | Sort-Object Length -Descending | Format-Table Name, @{Label = 'Size'; Expression = { '{0,8:N0} KB' -f ($_.Length / 1KB) } } -AutoSize

Write-Host ""
Write-Host "Install with: .\Releases\AresToys-$Channel-Setup.exe" -ForegroundColor Gray
Write-Host "Or unzip:     .\Releases\AresToys-$Channel-Portable.zip" -ForegroundColor Gray

# Pop Explorer at the artifacts so a double-click to install is one less step.
$releasesPath = Join-Path $repoRoot 'Releases'
if (Test-Path $releasesPath) { Start-Process explorer.exe $releasesPath }
