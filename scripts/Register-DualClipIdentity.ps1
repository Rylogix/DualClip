param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [string]$AppOutputPath,

    [switch]$ForceReinstall
)

$ErrorActionPreference = "Stop"

$packageName = "Rylogix.DualClip"
$publisher = "CN=87B3C267-8985-4CA9-B2A8-54EFF3C074C7"
$certificateFileBaseName = "Rylogix.DualClip"

function Get-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

function Ensure-BuildTools {
    param(
        [string]$RepoRoot
    )

    $projectPath = Join-Path $RepoRoot "build\BuildTools\BuildTools.csproj"
    dotnet restore $projectPath --nologo | Out-Host

    $version = "10.0.26100.7175"
    $packageRoot = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windows.sdk.buildtools\$version"

    if (!(Test-Path $packageRoot)) {
        throw "Microsoft.Windows.SDK.BuildTools $version was not restored to '$packageRoot'."
    }

    $makeAppx = Get-ChildItem $packageRoot -Recurse -Filter makeappx.exe |
        Where-Object { $_.FullName -match "\\x64\\" } |
        Select-Object -First 1 -ExpandProperty FullName

    $signTool = Get-ChildItem $packageRoot -Recurse -Filter signtool.exe |
        Where-Object { $_.FullName -match "\\x64\\" } |
        Select-Object -First 1 -ExpandProperty FullName

    if ([string]::IsNullOrWhiteSpace($makeAppx)) {
        throw "Could not find makeappx.exe under '$packageRoot'."
    }

    if ([string]::IsNullOrWhiteSpace($signTool)) {
        $signTool = "C:\Program Files (x86)\Microsoft SDKs\ClickOnce\SignTool\signtool.exe"
    }

    if (!(Test-Path $signTool)) {
        throw "Could not find signtool.exe."
    }

    return @{
        MakeAppx = $makeAppx
        SignTool = $signTool
    }
}

function Ensure-IdentityAssets {
    param(
        [string]$ManifestRoot
    )

    $assetsRoot = Join-Path $ManifestRoot "Assets"
    New-Item -ItemType Directory -Path $assetsRoot -Force | Out-Null

    $pngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+iQ6kAAAAASUVORK5CYII="

    foreach ($name in @("StoreLogo.png", "Square150x150Logo.png", "Square44x44Logo.png")) {
        $path = Join-Path $assetsRoot $name
        if (!(Test-Path $path)) {
            [IO.File]::WriteAllBytes($path, [Convert]::FromBase64String($pngBase64))
        }
    }
}

function Ensure-SigningCertificate {
    param(
        [string]$RepoRoot
    )

    $certRoot = Join-Path $RepoRoot "packaging\certs"
    New-Item -ItemType Directory -Path $certRoot -Force | Out-Null

    $pfxPath = Join-Path $certRoot "$certificateFileBaseName.pfx"
    $cerPath = Join-Path $certRoot "$certificateFileBaseName.cer"
    $passwordText = "dualclip-dev"
    $password = ConvertTo-SecureString -String $passwordText -AsPlainText -Force

    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $publisher } | Sort-Object NotAfter -Descending | Select-Object -First 1

    $hasCodeSigningUsage = $false

    if ($null -ne $cert) {
        $hasCodeSigningUsage = $cert.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq "1.3.6.1.5.5.7.3.3" } | Select-Object -First 1
    }

    if ($null -ne $cert -and -not $hasCodeSigningUsage) {
        Get-ChildItem Cert:\CurrentUser\My | Where-Object Thumbprint -eq $cert.Thumbprint | Remove-Item -Force
        Get-ChildItem Cert:\CurrentUser\TrustedPeople | Where-Object Thumbprint -eq $cert.Thumbprint | Remove-Item -Force -ErrorAction SilentlyContinue
        Get-ChildItem Cert:\CurrentUser\Root | Where-Object Thumbprint -eq $cert.Thumbprint | Remove-Item -Force -ErrorAction SilentlyContinue
        $cert = $null
    }

    if ($null -eq $cert) {
        $cert = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject $publisher `
            -KeyAlgorithm RSA `
            -KeyLength 2048 `
            -HashAlgorithm SHA256 `
            -KeyExportPolicy Exportable `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -NotAfter (Get-Date).AddYears(5)
    }

    Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $password -Force | Out-Null

    foreach ($store in @(
        "Cert:\CurrentUser\TrustedPeople"
    )) {
        if (-not (Get-ChildItem $store | Where-Object Thumbprint -eq $cert.Thumbprint)) {
            Import-Certificate -FilePath $cerPath -CertStoreLocation $store | Out-Null
        }
    }

    return @{
        PfxPath = $pfxPath
        PasswordText = $passwordText
    }
}

function Get-AppOutputPath {
    param(
        [string]$RepoRoot,
        [string]$Configuration,
        [string]$ProvidedPath
    )

    if (![string]::IsNullOrWhiteSpace($ProvidedPath)) {
        return (Resolve-Path $ProvidedPath).Path
    }

    $defaultPath = Join-Path $RepoRoot "src\DualClip.App\bin\$Configuration\net8.0-windows10.0.20348.0"

    if (!(Test-Path $defaultPath)) {
        throw "Could not find the app output directory at '$defaultPath'. Build the solution first."
    }

    return (Resolve-Path $defaultPath).Path
}

function Remove-ExistingPackage {
    $existing = Get-AppxPackage $packageName -ErrorAction SilentlyContinue

    if ($null -ne $existing) {
        $existing | Remove-AppxPackage -ErrorAction Stop
    }
}

$repoRoot = Get-RepoRoot
$appOutput = Get-AppOutputPath -RepoRoot $repoRoot -Configuration $Configuration -ProvidedPath $AppOutputPath
$appExe = Join-Path $appOutput "DualClip.App.exe"

if (!(Test-Path $appExe)) {
    throw "Could not find '$appExe'."
}

$manifestRoot = Join-Path $repoRoot "packaging\Identity"
$manifestPath = Join-Path $manifestRoot "AppxManifest.xml"

Ensure-IdentityAssets -ManifestRoot $manifestRoot
$tools = Ensure-BuildTools -RepoRoot $repoRoot
$cert = Ensure-SigningCertificate -RepoRoot $repoRoot

$artifactsRoot = Join-Path $repoRoot "artifacts\identity"
New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

$packagePath = Join-Path $artifactsRoot "$packageName.msix"

if (Test-Path $packagePath) {
    Remove-Item $packagePath -Force
}

& $tools.MakeAppx pack /o /d $manifestRoot /nv /p $packagePath | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "makeappx.exe failed with exit code $LASTEXITCODE."
}

& $tools.SignTool sign /fd SHA256 /f $cert.PfxPath /p $cert.PasswordText $packagePath | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "signtool.exe failed with exit code $LASTEXITCODE."
}

if ($ForceReinstall) {
    Remove-ExistingPackage
}

$existing = Get-AppxPackage $packageName -ErrorAction SilentlyContinue

if ($null -eq $existing) {
    Add-AppxPackage -Path $packagePath -ExternalLocation $appOutput | Out-Null
}
else {
    $resolvedInstall = try { (Resolve-Path $existing.InstallLocation -ErrorAction Stop).Path } catch { $existing.InstallLocation }

    if ($ForceReinstall -or $resolvedInstall -ne $appOutput) {
        Remove-ExistingPackage
        Add-AppxPackage -Path $packagePath -ExternalLocation $appOutput | Out-Null
    }
}

Write-Host "DualClip identity package is registered for '$appOutput'."
