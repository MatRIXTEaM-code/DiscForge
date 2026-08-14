@echo off
REM DiscForge - double-click to build the GUI app (Release) and launch it.
REM Runs the PowerShell script beside this file.
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-and-run.ps1" %*
if errorlevel 1 (
    echo.
    echo Build or launch failed. See the messages above.
    pause
)
endlocal
