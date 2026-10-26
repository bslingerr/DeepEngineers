@echo off
rem DeepEngineers: remove everything the installer recorded (UNINSTALL.md). Options: -RemoveWorlds -RemoveBackups -KeepData -DryRun
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Uninstall %*
exit /b %ERRORLEVEL%
