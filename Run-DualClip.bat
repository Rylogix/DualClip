@echo off
setlocal

cd /d "%~dp0"

echo Closing any running DualClip instance...
taskkill /IM DualClip.App.exe /F >nul 2>nul
timeout /t 1 /nobreak >nul

echo Building DualClip...
dotnet build DualClip.sln --nologo --verbosity:minimal
if errorlevel 1 (
    echo.
    echo Build failed.
    pause
    exit /b 1
)

echo Registering borderless capture identity...
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Launch-DualClipBorderless.ps1" -Configuration Debug
if errorlevel 1 (
    echo.
    echo Borderless launch failed.
    pause
    exit /b 1
)

exit /b 0
