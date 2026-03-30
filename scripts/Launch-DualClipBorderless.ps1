param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$packageName = "Rylogix.DualClip"

function Test-IdentityRegistration {
    param(
        [string]$ExpectedOutputPath
    )

    $existing = Get-AppxPackage $packageName -ErrorAction SilentlyContinue

    if ($null -eq $existing) {
        return $false
    }

    $resolvedInstall = try {
        (Resolve-Path $existing.InstallLocation -ErrorAction Stop).Path
    }
    catch {
        $existing.InstallLocation
    }

    return $resolvedInstall -eq $ExpectedOutputPath
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$appOutput = Join-Path $repoRoot "src\DualClip.App\bin\$Configuration\net8.0-windows10.0.20348.0"
$appExe = Join-Path $appOutput "DualClip.App.exe"

if (!(Test-Path $appExe)) {
    throw "Could not find '$appExe'. Build the solution first."
}

Get-Process DualClip.App -ErrorAction SilentlyContinue | Stop-Process -Force

if (-not (Test-IdentityRegistration -ExpectedOutputPath $appOutput)) {
    & (Join-Path $PSScriptRoot "Register-DualClipIdentity.ps1") -Configuration $Configuration -AppOutputPath $appOutput
}

Start-Process -FilePath $appExe -WorkingDirectory $appOutput
