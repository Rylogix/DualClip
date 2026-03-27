@echo off
setlocal

cd /d "%~dp0"

if not "%~1"=="" (
    powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Release-DualClip.ps1" %*
    exit /b %errorlevel%
)

echo DualClip Release
echo.
echo Enter a version like 0.3.0 to release that exact version.
echo Leave it blank to auto-bump the next patch version.
echo.
set /p "RELEASE_VERSION=Version: "

if defined RELEASE_VERSION (
    set "RELEASE_ARGS=-Version %RELEASE_VERSION%"
) else (
    set "RELEASE_ARGS=-Bump patch"
)

echo.
set /p "DRY_RUN=Run a dry run first? [Y/n]: "

if /i not "%DRY_RUN%"=="n" (
    echo.
    echo Running dry run...
    powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Release-DualClip.ps1" %RELEASE_ARGS% -DryRun
    if errorlevel 1 (
        echo.
        echo Dry run failed.
        pause
        exit /b 1
    )
)

echo.
set /p "CONFIRM=Push this release to GitHub now? [y/N]: "
if /i not "%CONFIRM%"=="y" (
    echo.
    echo Release cancelled.
    pause
    exit /b 0
)

echo.
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Release-DualClip.ps1" %RELEASE_ARGS%
set "EXIT_CODE=%errorlevel%"

echo.
if not "%EXIT_CODE%"=="0" (
    echo Release failed.
    pause
    exit /b %EXIT_CODE%
)

echo Release completed.
pause
exit /b 0
