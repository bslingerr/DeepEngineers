@echo off
rem DeepEngineers: backups, checks, then Subnautica (hidden renderer) and Space Engineers. Option: -Continue (load the last world)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0play.ps1" %*
exit /b %ERRORLEVEL%
