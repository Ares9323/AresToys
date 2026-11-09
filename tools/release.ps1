# One-shot release driver: read the csproj version, run tests, tag + push.
#
# Usage (from anywhere — the script chdir's to repo root):
#   pwsh tools/release.ps1                       # full flow, asks for confirmation
#   pwsh tools/release.ps1 -SkipTests            # skip the test pass (only when you JUST ran it)
#   pwsh tools/release.ps1 -SkipConfirm          # no interactive prompt — for scripted use
#   pwsh tools/release.ps1 -Version 0.1.8        # override csproj-derived version
#   pwsh tools/release.ps1 -Force                # allow a dirty working tree (uncommitted changes)
#
# What it does, in order:
#   1. Reads <Version> from src/AresToys.App/AresToys.App.csproj, strips '-dev' suffix.
#   2. Verifies the working tree is clean (uncommitted changes are usually a mistake here —
#      the tag should point at a deliberate state). -Force overrides.
#   3. Verifies the tag doesn't already exist locally or on origin. Re-releasing a version
#      would be a confusing mess (Velopack package version collision, ambiguous tag history).
#   4. Runs tools/test-local.ps1 unless -SkipTests. A failure stops the flow here.
#   5. Confirms with the user (unless -SkipConfirm), creates the annotated tag, pushes it.
#   6. The push fires the on:push:tags trigger in .github/workflows/release.yml — vpk packs
#      and drafts a GitHub Release. Watch the Actions tab to track / approve.

[CmdletBinding()]
param(
    [string]$Version,
    [switch]$SkipTests,
    [switch]$SkipConfirm,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

# ── Step 1: resolve the version ─────────────────────────────────────────────────────
if (-not $Version) {
    $csprojPath = Join-Path $repoRoot 'src/AresToys.App/AresToys.App.csproj'
    if (-not (Test-Path $csprojPath)) { throw "csproj not found at $csprojPath." }
    [xml]$csprojXml = Get-Content $csprojPath
    $projVersion = $csprojXml.Project.PropertyGroup |
        ForEach-Object { $_.Version } |
        Where-Object { $_ } |
        Select-Object -First 1
    if (-not $projVersion) { throw "No <Version> element in $csprojPath." }
    $Version = $projVersion -replace '-dev$', ''
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[\w\.]+)?$') {
    throw "Version '$Version' doesn't look like SemVer (X.Y.Z or X.Y.Z-suffix)."
}
$tag = "v$Version"
Write-Host "==> Release target: $tag" -ForegroundColor Cyan

# ── Step 2: working tree must be clean ──────────────────────────────────────────────
$status = git status --porcelain
if ($status -and -not $Force) {
    Write-Host "Working tree is dirty:" -ForegroundColor Red
    Write-Host $status
    throw "Commit / stash before releasing, or pass -Force to override (rarely a good idea)."
}

# ── Step 3: tag must not already exist ──────────────────────────────────────────────
$localTag = git tag -l $tag
if ($localTag) { throw "Tag $tag already exists locally — bump the csproj version first." }
# `git ls-remote` exit code is 0 even when the tag is absent; we just check the output.
$remoteTag = git ls-remote --tags origin "refs/tags/$tag"
if ($remoteTag) { throw "Tag $tag already exists on origin — bump the csproj version first." }

# ── Step 4: run tests ───────────────────────────────────────────────────────────────
if (-not $SkipTests) {
    Write-Host "==> Running test suite (tools/test-local.ps1)" -ForegroundColor Cyan
    & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test-local.ps1')
    if ($LASTEXITCODE -ne 0) { throw "Tests failed — fix them before tagging the release." }
} else {
    Write-Host "==> Skipping tests (-SkipTests)" -ForegroundColor Yellow
}

# ── Step 5: confirm + tag + push ────────────────────────────────────────────────────
if (-not $SkipConfirm) {
    Write-Host ""
    Write-Host "About to:" -ForegroundColor Yellow
    Write-Host "  git tag -a $tag -m 'Release $Version'"
    Write-Host "  git push origin $tag"
    Write-Host ""
    $reply = Read-Host "Proceed? [y/N]"
    if ($reply -notmatch '^[yY]') {
        Write-Host "Aborted." -ForegroundColor Yellow
        exit 0
    }
}

Write-Host "==> git tag -a $tag" -ForegroundColor Cyan
git tag -a $tag -m "Release $Version"
if ($LASTEXITCODE -ne 0) { throw "git tag failed" }

Write-Host "==> git push origin $tag" -ForegroundColor Cyan
git push origin $tag
if ($LASTEXITCODE -ne 0) {
    # The local tag was created but the push failed — clean it up so the next run starts
    # from a known state instead of tripping the "tag already exists locally" guard.
    Write-Host "Push failed — removing the local tag so the next attempt can recreate it." -ForegroundColor Yellow
    git tag -d $tag | Out-Null
    throw "git push origin $tag failed"
}

Write-Host ""
Write-Host "==> Tag pushed. GitHub Actions should now be packing $tag." -ForegroundColor Green
Write-Host "    Watch: https://github.com/$(git config --get remote.origin.url | ForEach-Object { ($_ -replace '.*github\.com[/:]', '') -replace '\.git$', '' })/actions"
