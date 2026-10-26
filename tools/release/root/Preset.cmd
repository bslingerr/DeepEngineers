@echo off
rem DeepEngineers: the performance preset of this PC (low, mid, high): shows the one in use and lets you change it. No game is started.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0play.ps1" -Preset %*
exit /b %ERRORLEVEL%
