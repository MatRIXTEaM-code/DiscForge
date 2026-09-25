@echo off
rem Double-click: push to GitHub, build DiscForge, start it and open the donation page.
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0do-everything.ps1" %*
