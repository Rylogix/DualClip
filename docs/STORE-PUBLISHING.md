# DualClip Store Publishing

DualClip now supports two release artifacts from the same codebase:

- Portable GitHub release: `DualClip.App.exe`
- Packaged release: `DualClip-<version>-x64.msix` and `DualClip-<version>-x64.msixupload`

## Checked-in Store identity

The repo now defaults to the real Partner Center identity:

- `Package/Identity/Name`: `Rylogix.DualClip`
- `Package/Identity/Publisher`: `CN=87B3C267-8985-4CA9-B2A8-54EFF3C074C7`
- `Package/Properties/DisplayName`: `DualClip`
- `Package/Properties/PublisherDisplayName`: `Rylogix`

GitHub Actions repository variables can still override these values if needed:

- `DUALCLIP_PACKAGE_NAME`
- `DUALCLIP_PACKAGE_PUBLISHER`
- `DUALCLIP_PACKAGE_DISPLAYNAME`
- `DUALCLIP_PACKAGE_PUBLISHERDISPLAYNAME`

Store metadata that is not sourced from this repo also needs to stay valid in Partner Center:

- Website URL: `https://rylogix.com`

## Optional signing for CI MSIX builds

If you want the GitHub workflow to produce a signed MSIX, add these repository secrets:

- `DUALCLIP_MSIX_CERT_BASE64`
- `DUALCLIP_MSIX_CERT_PASSWORD`

`DUALCLIP_MSIX_CERT_BASE64` should be the base64-encoded contents of your `.pfx` signing certificate.

If these secrets are not set, the workflow still builds the portable `.exe` and the Store upload bundle.

## Local identity registration

The local borderless-capture registration scripts now use the same package identity and generate a matching self-signed certificate for development installs.

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
