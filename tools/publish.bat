@echo off
rem ============================================================================
rem  tools\publish.bat - Replica il "Publish to Folder" di Visual Studio da riga
rem  di comando. Usa lo STESSO profilo (.pubxml) che usa Visual Studio, quindi se
rem  modifichi il profilo dall'IDE questo script resta automaticamente allineato.
rem
rem  Profilo: src\AresToys.App\Properties\PublishProfiles\FolderProfile.pubxml
rem    -> Configuration = Release
rem    -> Output        = src\AresToys.App\bin\Release\net10.0-windows10.0.19041.0\publish\
rem
rem  Per pubblicare con una versione specifica (come fa la release):
rem    tools\publish.bat 0.1.25
rem ============================================================================
setlocal

rem Lo script vive in tools\ ; risali alla root del repo (un livello sopra).
cd /d "%~dp0.."
set "REPO=%CD%"

set "PROJECT=src\AresToys.App\AresToys.App.csproj"
set "OUTDIR=%REPO%\src\AresToys.App\bin\Release\net10.0-windows10.0.19041.0\publish"

rem Argomento opzionale: versione da incidere nel build (default = quella nel .csproj).
set "VERSIONARG="
if not "%~1"=="" set "VERSIONARG=-p:Version=%~1"

echo.
echo === Publishing AresToys (Release, FolderProfile) ===
if not "%~1"=="" echo     Version override: %~1
echo.

dotnet publish "%PROJECT%" -p:PublishProfile=FolderProfile %VERSIONARG%
if errorlevel 1 (
    echo.
    echo *** Publish FAILED ***
    pause
    exit /b 1
)

echo.
echo === Publish completed ===
echo Output: %OUTDIR%
start "" explorer "%OUTDIR%"
pause
