@echo off
REM DiscForge — build and launch the WinForms desktop app.
REM Usage:  run-app.bat            (build Release and run)
REM         run-app.bat Debug      (build Debug and run)
setlocal
cd /d "%~dp0"

set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Release"

echo Building DiscForge.App (%CONFIG%)...
dotnet build "src\DiscForge.App\DiscForge.App.csproj" -c %CONFIG%
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)

set "EXE=src\DiscForge.App\bin\%CONFIG%\net8.0-windows\DiscForge.exe"
if not exist "%EXE%" (
    echo Executable not found at "%EXE%".
    exit /b 1
)

echo Launching %EXE%
start "" "%EXE%"
endlocal
