@echo off
setlocal

cd /d "%~dp0"

powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Publish-DualClip-GitHubRelease.ps1" %*
exit /b %errorlevel%
