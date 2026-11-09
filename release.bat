@echo off
:: Thin shim — double-click to run the full release flow (read csproj version → test →
:: tag → push). All the actual logic lives in tools/release.ps1; this exists so users who
:: don't live in pwsh can still kick a release without typing the long command.
::
:: Pass through any args (e.g. release.bat -SkipTests / -Version 0.1.8 / -Force).

pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\release.ps1" %*
exit /b %ERRORLEVEL%
