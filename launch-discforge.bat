@echo off
rem DiscForge — double-click launcher. Builds (if needed) and starts the app.
rem All options pass through, e.g.:  launch-discforge.bat -Quick -Test
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0launch-discforge.ps1" %*
if errorlevel 1 pause
