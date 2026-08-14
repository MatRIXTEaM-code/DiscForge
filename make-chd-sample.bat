@echo off
rem Double-click to generate a CHD sample for DiscForge validation.
rem See make-chd-sample.ps1 for options (custom cue, chdman path).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-chd-sample.ps1" %*
pause
