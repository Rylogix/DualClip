@echo off
setlocal

cd /d "%~dp0"

set "APP_EXE=%~dp0src\DualClip.App\bin\Debug\net8.0-windows10.0.20348.0\DualClip.App.exe"

if not exist "%APP_EXE%" (
    echo.
    echo Could not find "%APP_EXE%".
    echo Build the app first or run Run-DualClip.bat once.
    pause
    exit /b 1
)

echo Closing any running DualClip instance...
taskkill /IM DualClip.App.exe /F >nul 2>nul
timeout /t 1 /nobreak >nul

echo Launching DualClip...
start "" "%APP_EXE%"
exit /b 0
