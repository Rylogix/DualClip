@echo off
setlocal

cd /d "%~dp0"

set "PUBLISH_DIR=%~dp0publish\DualClip.SingleFile"

echo Closing any running DualClip instance...
taskkill /IM DualClip.App.exe /F >nul 2>nul
timeout /t 1 /nobreak >nul

echo Publishing DualClip as a single-file executable to "%PUBLISH_DIR%"...
dotnet publish src\DualClip.App\DualClip.App.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:DebugSymbols=false ^
  -p:DebugType=None ^
  --nologo ^
  -o "%PUBLISH_DIR%"

if errorlevel 1 (
    echo.
    echo Single-file publish failed.
    pause
    exit /b 1
)

echo.
echo Published successfully.
echo Send this file:
echo %PUBLISH_DIR%\DualClip.App.exe
echo.
echo Note:
echo - This single-file build includes ffmpeg automatically.
echo - Borderless capture may still require the identity-backed launch path, so friends may still see the Windows yellow capture border.
exit /b 0
