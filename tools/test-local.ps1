# Local test runner — mirrors what .github/workflows/ci.yml runs on PRs. Use this before
# cutting a release (tag push or workflow_dispatch) so you don't discover a regression after
# Velopack has already drafted the GitHub Release.
#
# Usage (from the repo root):
#   pwsh tools/test-local.ps1                       # full suite, Release configuration
#   pwsh tools/test-local.ps1 -Configuration Debug  # faster build, same coverage
#   pwsh tools/test-local.ps1 -Project Storage      # single project (matches *Storage*Tests)
#   pwsh tools/test-local.ps1 -SkipBuild            # reuse last build output
#   pwsh tools/test-local.ps1 -NoRestore            # skip nuget restore (CI-cached scenarios)
#
# Exit code is non-zero on any test failure so this can gate a release script:
#   pwsh tools/test-local.ps1; if ($LASTEXITCODE -eq 0) { git tag v0.1.8 && git push origin v0.1.8 }

[CmdletBinding()]
param(
    # Release matches CI more closely (some bugs only surface with optimizations on); Debug
    # is faster to iterate on. Default Release so "is this good enough to ship?" is the
    # default question.
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # Substring filter against the test-project name. Empty = run every *.Tests project under
    # tests/. Useful when iterating on one area: "-Project Storage" runs only
    # AresToys.Storage.Tests, "-Project Editor" runs only AresToys.Editor.Tests, etc.
    [string]$Project = '',

    # Reuse the previous build output. Saves ~15s when iterating on test code only and the
    # solution hasn't changed since the last run.
    [switch]$SkipBuild,

    # Skip the NuGet restore step. Only safe when packages haven't changed since the previous
    # build — the build itself would fail loudly otherwise.
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$sln = Join-Path $repoRoot 'AresToys.sln'
if (-not (Test-Path $sln)) { throw "Solution not found at $sln. Run this from a fresh clone or check the repo layout." }

if (-not $NoRestore -and -not $SkipBuild) {
    Write-Host "==> dotnet restore" -ForegroundColor Cyan
    dotnet restore $sln | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }
}

if (-not $SkipBuild) {
    Write-Host "==> dotnet build ($Configuration)" -ForegroundColor Cyan
    $buildArgs = @('build', $sln, '--configuration', $Configuration)
    if ($NoRestore) { $buildArgs += '--no-restore' }
    & dotnet @buildArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Build failed — fix the compile errors before running tests." }
}

# Resolve which test projects to run. Filter is a case-insensitive substring on the project
# file name so "-Project Storage" matches AresToys.Storage.Tests.csproj. Empty filter = every
# project under tests/.
$allTestProjects = Get-ChildItem -Path (Join-Path $repoRoot 'tests') -Recurse -Filter '*.Tests.csproj'
if ($Project) {
    $filtered = $allTestProjects | Where-Object { $_.BaseName -like "*$Project*" }
    if (-not $filtered) {
        $names = ($allTestProjects | ForEach-Object { $_.BaseName }) -join ', '
        throw "No test projects match '*$Project*'. Available: $names"
    }
    $allTestProjects = $filtered
}

Write-Host "==> Running $($allTestProjects.Count) test project(s)" -ForegroundColor Cyan
foreach ($proj in $allTestProjects) {
    Write-Host ""
    Write-Host "--- $($proj.BaseName) ---" -ForegroundColor Yellow
    # --no-build because we already built above (or the user passed -SkipBuild). Speeds up the
    # per-project loop ~3x and avoids a redundant compile pass.
    $testArgs = @('test', $proj.FullName, '--configuration', $Configuration, '--no-build', '--logger', 'console;verbosity=normal')
    & dotnet @testArgs | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "FAILED: $($proj.BaseName) — see output above. Stopping here." -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

Write-Host ""
Write-Host "==> All test projects passed." -ForegroundColor Green
