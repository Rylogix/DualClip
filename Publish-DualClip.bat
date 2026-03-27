@echo off
setlocal

cd /d "%~dp0"

set "PUBLISH_DIR=%~dp0publish\DualClip"

echo Closing any running DualClip instance...
taskkill /IM DualClip.App.exe /F >nul 2>nul
timeout /t 1 /nobreak >nul

echo Publishing DualClip to "%PUBLISH_DIR%"...
dotnet publish src\DualClip.App\DualClip.App.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained false ^
  --nologo ^
  -o "%PUBLISH_DIR%"

if errorlevel 1 (
    echo.
    echo Publish failed.
    pause
    exit /b 1
)

echo.
echo Published successfully.
echo Run this file next time:
echo %PUBLISH_DIR%\DualClip.App.exe
echo.
echo To register borderless capture for the published build, run:
echo powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Register-DualClipIdentity.ps1" -Configuration Release -AppOutputPath "%PUBLISH_DIR%"
exit /b 0
