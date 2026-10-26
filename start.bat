@echo off
setlocal EnableExtensions
rem DeepEngineers start: the latest deployment, fresh backups, then both games. Double-click, no copy-paste.
rem 1. refuses while a game runs 2. build.ps1 (both plugins) + deploy_worldmod.ps1 3. launcher.py backup (3 sets)
rem 4. launcher.py run (preflight, Subnautica hidden first, then Space Engineers through Pulsar; -continue = last world)
rem Extra words are passed to `run` (for example: start.bat --refresh-saves-backup).
cd /d "%~dp0"
title DeepEngineers start
echo == DeepEngineers start %DATE% %TIME%

tasklist /FI "IMAGENAME eq SpaceEngineers.exe" 2>nul | find /I "SpaceEngineers.exe" >nul && goto :running
tasklist /FI "IMAGENAME eq Legacy.exe"         2>nul | find /I "Legacy.exe"         >nul && goto :running
tasklist /FI "IMAGENAME eq Subnautica.exe"     2>nul | find /I "Subnautica.exe"     >nul && goto :running

echo == 1/3 latest deployment: build + deploy (both plugins, then the world mod)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
set "BUILD=%ERRORLEVEL%"
if "%BUILD%"=="0" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\worldmod\deploy_worldmod.ps1"
    set "BUILD=%ERRORLEVEL%"
)
dotnet build-server shutdown >nul 2>&1
if not "%BUILD%"=="0" (
    echo.
    echo BUILD OR DEPLOY FAILED ^(exit %BUILD%^): the game would run the PREVIOUS deployment.
    choice /C ON /N /M "Launch the previous deployment anyway? [O]ui / [N]on: "
    if errorlevel 2 goto :fail
)

echo == 1b/3 world copy for the Star System card ^(%%LOCALAPPDATA%%\DeepEngineers\worlds, the local mod^)
python -I "%~dp0tools\worldmod\make_scenario_world.py" --to-local --local-mod --force >nul 2>&1
if errorlevel 1 echo WORLD COPY FAILED ^(the Star System card keeps SE's own world^)
echo == 2/3 backups ^(se-appdata, subnautica-saves, subnautica-locallow; renewed when older than 7 days, 2 kept; start.bat --refresh-saves-backup forces^)
python -I "%~dp0src\DeepEngineers.Launcher\launcher.py" backup --keep 2 --max-age-days 7
if errorlevel 1 (
    echo BACKUP FAILED: not launching.
    goto :fail
)

echo == 3/3 run ^(the window stays until both games are closed^)
python -I "%~dp0src\DeepEngineers.Launcher\launcher.py" run %*
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
    echo run ended with exit %RC% ^(read the lines above^)
    goto :fail
)
exit /b 0

:running
echo A game is still running ^(Space Engineers, Legacy or Subnautica^): close it first, then start again.
goto :fail

:fail
echo.
pause
exit /b 1
