# DualClip Store Publishing

DualClip now supports two release artifacts from the same codebase:

- Portable GitHub release: `DualClip.App.exe`
- Packaged release: `DualClip-<version>-x64.msix` and `DualClip-<version>-x64.msixupload`

## What still needs to come from Partner Center

Before submitting to Microsoft Store, set the real package identity values in GitHub Actions repository variables:

- `DUALCLIP_PACKAGE_NAME`
- `DUALCLIP_PACKAGE_PUBLISHER`
- `DUALCLIP_PACKAGE_DISPLAYNAME`
- `DUALCLIP_PACKAGE_PUBLISHERDISPLAYNAME`

The checked-in defaults are development values only.

## Optional signing for CI MSIX builds

If you want the GitHub workflow to produce a signed MSIX, add these repository secrets:

- `DUALCLIP_MSIX_CERT_BASE64`
- `DUALCLIP_MSIX_CERT_PASSWORD`

`DUALCLIP_MSIX_CERT_BASE64` should be the base64-encoded contents of your `.pfx` signing certificate.

If these secrets are not set, the workflow still builds the portable `.exe` and the Store upload bundle.

## Local commands

Build the portable release asset:

```powershell
.\scripts\Publish-DualClip-GitHubRelease.ps1 -Version 0.6.0
```

Build the portable release asset and MSIX artifacts:

```powershell
.\scripts\Publish-DualClip-GitHubRelease.ps1 -Version 0.6.0 -IncludeMsix
```

Build only the MSIX artifacts:

```powershell
.\scripts\Build-DualClipMsix.ps1 -Version 0.6.0
```

## Store-specific app behavior

When DualClip is running as a packaged app, in-app GitHub self-updates are disabled and the Settings update card switches to Store-managed messaging. Portable builds continue using the GitHub updater.
