@echo off
rem Double-click this to build DiscForge (app .exe + installer .exe).
rem It runs build-discforge.ps1 with the execution policy bypassed for this one run,
rem so Windows' script policy can't silently block it, and the window stays open.
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-discforge.ps1" %*
