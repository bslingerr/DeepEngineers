@echo off
rem DeepEngineers: install, or repair an install (run it again). README.md has the steps.
rem Options: -WorldName "My world" -PulsarDir "D:\Pulsar" -OfflineDir "D:\downloads" -SteamId 7656... -Check
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
exit /b %ERRORLEVEL%
