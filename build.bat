@echo off
rem DiscForge — full local build (Core, Devices, CLI, and the WinForms GUI) + tests.
rem Pass through any flags, e.g.:  build.bat -Publish   |   build.bat -Run
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
