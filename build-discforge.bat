@echo off
setlocal
rem Double-click this to build DiscForge (app .exe + installer .exe).
rem The window always stays open at the end, so any error can be read.

set "SCRIPT=%~dp0build-discforge.ps1"
if not exist "%SCRIPT%" set "SCRIPT=C:\dev\DiscForge\build-discforge.ps1"
if not exist "%SCRIPT%" (
    echo.
    echo Could not find build-discforge.ps1 next to this file or in C:\dev\DiscForge.
    echo Run the copy of build-discforge.bat that is inside your DiscForge folder.
    echo.
    pause
    exit /b 1
)

echo Running: %SCRIPT%
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" (
    echo PowerShell finished with error code %RC%. Scroll up to see what went wrong.
    echo If there is no message above, send a screenshot of this window.
)
echo.
pause
exit /b %RC%
