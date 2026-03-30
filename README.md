# DualClip MVP

DualClip is a Windows-only desktop MVP for instant replay clipping across two monitors.

It continuously captures two selected displays with Windows Graphics Capture, writes rolling 1-second video segments for each display, and saves the most recent replay window to `.mp4` when a global hotkey is pressed.

The current UI runs in dark mode, plays system clip sounds for save events, minimizes to the desktop tray instead of fully exiting when you close the main window, includes a borderless-capture launch path backed by package identity, and can now self-update from GitHub Releases.

## Tech Stack

- C#
- .NET 8
- WPF
- Windows Graphics Capture
- FFmpeg as an external executable
- Win32 `RegisterHotKey`

## Prerequisites

- Windows 11 or newer for the borderless-capture launch path
- Windows build `10.0.20348.0` or newer for borderless Windows Graphics Capture support
- .NET 8 SDK or newer
- Two connected monitors
- `ffmpeg.exe` at:

```text
C:\Users\rylan\Downloads\RLXProjects\DualClip\Tools\ffmpeg.exe
```

## Solution Layout

```text
DualClip.sln
src/
  DualClip.App
  DualClip.Core
  DualClip.Capture
  DualClip.Buffering
  DualClip.Encoding
  DualClip.Infrastructure
Tools/
  ffmpeg.exe
```

## Build

From the repository root:

```powershell
dotnet restore DualClip.sln
dotnet build DualClip.sln
```

## Run

From the repository root:

```powershell
dotnet run --project src\DualClip.App\DualClip.App.csproj
```

## No-Command Launch

Double-click:

```text
Run-DualClip.bat
```

That script now:

- does an incremental `dotnet build`
- prompts for admin once so it can register DualClip's local identity package and trust the dev certificate
- launches the built exe from the identity-backed output folder so Windows can grant borderless capture

If you cancel the UAC prompt, DualClip will not get the borderless permission for that launch.

If you want a cleaner release-style output folder, double-click:

```text
Publish-DualClip.bat
```

That creates:

```text
publish\DualClip\DualClip.App.exe
```

You can then launch that `.exe` directly without opening a terminal.

If you want a single executable you can send to someone else, double-click:

```text
Publish-DualClip-SingleFile.bat
```

That creates:

```text
publish\DualClip.SingleFile\DualClip.App.exe
```

This build is self-contained and now carries `ffmpeg.exe` with it automatically, so your friend does not need your repo layout or a separate `Tools\ffmpeg.exe`.

## Updater

- DualClip now checks `Rylogix/DualClip` GitHub Releases on startup and automatically installs a newer portable build when one is available.
- The updater expects a stable GitHub release tag like `v0.2.0`.
- The release should include a portable executable asset named like `DualClip.App-0.2.0.exe` or `DualClip.App.exe`.
- On startup, DualClip downloads the new `.exe`, closes itself, swaps the executable, and relaunches automatically.
- The Settings tab still lets you check manually and trigger installation if the automatic attempt fails.
- Self-update needs write access to the folder where `DualClip.App.exe` is running. If you run it from a protected folder, update manually or move it somewhere writable first.

To build the GitHub release asset:

```powershell
.\Publish-DualClip-GitHubRelease.bat -Version 0.2.0
```

To build and upload the asset with GitHub CLI:

```powershell
.\Publish-DualClip-GitHubRelease.bat -Version 0.2.0 -Upload
```

That script publishes the app as a single-file `win-x64` executable, stamps the assembly version used by the updater, and writes the release asset here:

```text
artifacts\github-release\0.2.0\DualClip.App-0.2.0.exe
```

## GitHub Actions Releases

- `.github/workflows/release.yml` now builds and publishes release assets automatically.
- Push a tag like `v0.2.0` and GitHub Actions will build versioned assets like `DualClip.App-0.2.0.exe` and `DualClip-0.2.0-x64.msix`, create or update the matching GitHub release, and upload them.
- You can also run the workflow manually with `workflow_dispatch` and provide a `version` input.
- The workflow downloads `ffmpeg.exe` during CI because `Tools\ffmpeg.exe` is intentionally not stored in the repo.

Tag-based release example:

```powershell
git tag v0.2.0
git push origin v0.2.0
```

## Borderless Capture

- The yellow Windows capture border can only be removed through the packaged Windows Graphics Capture path.
- DualClip now uses a lightweight external-location identity package for that.
- The package registration is handled by [Run-DualClip.bat](C:/Users/rylan/Downloads/RLXProjects/DualClip/Run-DualClip.bat) and [scripts/Register-DualClipIdentity.ps1](C:/Users/rylan/Downloads/RLXProjects/DualClip/scripts/Register-DualClipIdentity.ps1).
- The first registration installs a local self-signed development certificate and therefore requires admin approval.
- If you launch the app directly with `dotnet run`, you may still see the yellow border because that path does not guarantee package identity.
- The standalone single-file `.exe` is the easiest build to share, but it does not carry package identity by itself, so friends may still see the yellow border even though capture still works.

## Tray Behavior

- Closing the main window sends DualClip to the desktop tray instead of exiting.
- If capture is already running, capture and hotkeys stay active while the window is hidden.
- Double-click the tray icon to reopen the window.
- Use the tray icon `Exit` menu item to fully shut down the app.

## How To Use

1. Launch the app.
2. Pick `Monitor A` and `Monitor B`.
3. Set replay length, FPS target, and clip quality (`Original`, `1440p`, `1080p`, or `720p`).
4. Choose whether clips should include `System Audio`, `Microphone`, or no audio.
5. If you choose microphone audio, select the active microphone device.
6. Choose output folders for both monitors.
7. Confirm the hotkeys for:
   - Monitor A clip
   - Monitor B clip
   - Both monitors together
8. Verify the FFmpeg path.
9. Click `Start Capture`.
10. Press the configured hotkeys while the app is running.

Saved clips are written as `.mp4` files into the configured output folders.
DualClip also plays a short system sound when a clip save is queued, completed, or fails.

## Local Files Used By The App

- Config file:

```text
%LocalAppData%\DualClip\config.json
```

- Rolling segment buffers:

```text
%LocalAppData%\DualClip\buffers\A
%LocalAppData%\DualClip\buffers\B
%LocalAppData%\DualClip\buffers\Audio
```

## Current MVP Limitations

- Borderless capture depends on Windows granting `graphicsCaptureWithoutBorder` for the identity-backed launch path. If the registration step is skipped or another app is simultaneously capturing the same display with a required border, Windows can still show the border.
- Audio is one shared source for both monitor clips. If enabled, the same system-audio or microphone track is muxed into clips from Monitor A and Monitor B.
- Audio source options are intentionally simple for now: `Disabled`, `System Audio`, or `Microphone`.
- Active replay data is stored as 1-second segments, so the newest partial second may not make it into a saved clip.
- If a monitor resolution changes while capture is running, restart capture.
- No automatic FFmpeg download or installer flow.
- Borderless capture and easiest sharing pull in opposite directions right now: the easiest file to share is the standalone single-file build, but the cleanest no-border capture path still depends on package identity/registration.
- The updater targets the portable single-file GitHub release asset. It does not update the identity-backed packaged launch path.
- No live preview window.
- Hotkey registration errors depend on Windows availability of the requested combination.
- Capture and clip assembly are optimized for simplicity and debuggability, not final production performance.

## Next Improvements

- Replace the development certificate flow with a cleaner signed installer flow.
- Add audio mixing for `System Audio + Microphone`.
- Add per-device audio routing and level controls.
- Add automatic recovery if FFmpeg exits unexpectedly.
- Add configurable segment length.
- Add per-monitor preview thumbnails.
- Add better validation and in-app diagnostics logging.
- Add publish packaging for a cleaner standalone deployment.

## Quick Test Commands

Build:

```powershell
dotnet build DualClip.sln
```

Run:

```powershell
dotnet run --project src\DualClip.App\DualClip.App.csproj
```

Smoke-check the built executable:

```powershell
.\src\DualClip.App\bin\Debug\net8.0-windows10.0.20348.0\DualClip.App.exe
```

Double-click launcher:

```text
Run-DualClip.bat
```

Release-style publish:

```text
Publish-DualClip.bat
publish\DualClip\DualClip.App.exe
```

Single-file publish for sharing:

```text
Publish-DualClip-SingleFile.bat
publish\DualClip.SingleFile\DualClip.App.exe
```
