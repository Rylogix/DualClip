@echo off
setlocal

cd /d "%~dp0"

set "DUALCLIP_PACKAGE_NAME=Rylogix.DualClip"
set "DUALCLIP_PACKAGE_PUBLISHER=CN=87B3C267-8985-4CA9-B2A8-54EFF3C074C7"
set "DUALCLIP_PACKAGE_PUBLISHERDISPLAYNAME=Rylogix"

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
set "RELEASE_NOTES_ARG="
set "BUILD_RELEASE_ARTIFACTS_ARG="

echo.
set /p "INCLUDE_NOTES=Paste release notes now? [y/N]: "

if /i "%INCLUDE_NOTES%"=="y" (
    for /f "usebackq delims=" %%I in (`powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$notesPath = Join-Path $env:TEMP ('dualclip-release-notes-' + [guid]::NewGuid().ToString('N') + '.md'); Write-Host ''; Write-Host 'Paste release notes below. Type END on its own line when finished.'; $lines = New-Object System.Collections.Generic.List[string]; while ($true) { $line = Read-Host; if ($line -eq 'END') { break }; $null = $lines.Add($line) }; [System.IO.File]::WriteAllLines($notesPath, $lines); Write-Output $notesPath"`) do set "RELEASE_NOTES_FILE=%%I"
)

if defined RELEASE_NOTES_FILE (
    set RELEASE_NOTES_ARG=-NotesFile "%RELEASE_NOTES_FILE%"
)

echo.
set /p "BUILD_RELEASE_ARTIFACTS=Build the portable EXE and MSIX locally before pushing? [Y/n]: "
if /i not "%BUILD_RELEASE_ARTIFACTS%"=="n" (
    set "BUILD_RELEASE_ARTIFACTS_ARG=-BuildReleaseArtifacts"
)

echo.
set /p "DRY_RUN=Run a dry run first? [Y/n]: "

if /i not "%DRY_RUN%"=="n" (
    echo.
    echo Running dry run...
    powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Release-DualClip.ps1" %RELEASE_ARGS% %RELEASE_NOTES_ARG% %BUILD_RELEASE_ARTIFACTS_ARG% -DryRun
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
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Release-DualClip.ps1" %RELEASE_ARGS% %RELEASE_NOTES_ARG% %BUILD_RELEASE_ARTIFACTS_ARG%
set "EXIT_CODE=%errorlevel%"

if defined RELEASE_NOTES_FILE if exist "%RELEASE_NOTES_FILE%" del /q "%RELEASE_NOTES_FILE%" >nul 2>nul

echo.
if not "%EXIT_CODE%"=="0" (
    echo Release failed.
    pause
    exit /b %EXIT_CODE%
)

echo Release completed.
pause
exit /b 0
