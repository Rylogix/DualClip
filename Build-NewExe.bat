@echo off
setlocal

cd /d "%~dp0"

powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-NewExe.ps1" %*
set "EXITCODE=%errorlevel%"

if not "%EXITCODE%"=="0" (
    echo.
    echo Build-NewExe failed with exit code %EXITCODE%.
    echo Press any key to close this window...
    pause >nul
)

exit /b %EXITCODE%
