[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$RuntimeIdentifier = "win-x64",

    [string]$OutputRoot,

    [string]$PackageName = $env:DUALCLIP_PACKAGE_NAME,

    [string]$Publisher = $env:DUALCLIP_PACKAGE_PUBLISHER,

    [string]$DisplayName = $env:DUALCLIP_PACKAGE_DISPLAYNAME,

    [string]$PublisherDisplayName = $env:DUALCLIP_PACKAGE_PUBLISHERDISPLAYNAME,

    [string]$Description = "DualClip screen capture and clip editing",

    [string]$CertificatePath = $env:DUALCLIP_MSIX_CERT_PATH,

    [string]$CertificatePassword = $env:DUALCLIP_MSIX_CERT_PASSWORD,

    [string]$CertificateBase64 = $env:DUALCLIP_MSIX_CERT_BASE64
)

$ErrorActionPreference = "Stop"

function Resolve-NormalizedVersion {
    param([string]$RawVersion)

    $normalizedVersion = $RawVersion.Trim()
    if ($normalizedVersion.StartsWith('v')) {
        $normalizedVersion = $normalizedVersion.Substring(1)
    }

    if ($normalizedVersion -notmatch '^\d+\.\d+\.\d+([\-+][0-9A-Za-z\.-]+)?$') {
        throw "Version '$RawVersion' is not a supported semantic version. Use values like 0.2.0 or v0.2.0."
    }

    return $normalizedVersion
}

function Get-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

function Get-PackageArchitecture {
    param([string]$Rid)

    if ($Rid -match 'x64') {
        return 'x64'
    }

    if ($Rid -match 'arm64') {
        return 'arm64'
    }

    if ($Rid -match 'x86') {
        return 'x86'
    }

    throw "Unsupported RuntimeIdentifier '$Rid' for MSIX packaging."
}

function Ensure-BuildTools {
    param([string]$RepoRoot)

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

function Set-RegexValue {
    param(
        [string]$Content,
        [string]$Pattern,
        [string]$Replacement
    )

    return [regex]::Replace($Content, $Pattern, $Replacement, [System.Text.RegularExpressions.RegexOptions]::Singleline)
}

function New-TemporaryCertificateFile {
    param(
        [string]$ArtifactsRoot,
        [string]$Base64Value
    )

    $certificatePath = Join-Path $ArtifactsRoot "release-signing.pfx"
    [IO.File]::WriteAllBytes($certificatePath, [Convert]::FromBase64String($Base64Value))
    return $certificatePath
}

$normalizedVersion = Resolve-NormalizedVersion -RawVersion $Version
$appxVersion = "$normalizedVersion.0"
$repoRoot = Get-RepoRoot
Set-Location $repoRoot

if ([string]::IsNullOrWhiteSpace($PackageName)) {
    $PackageName = "Rylogix.DualClip"
}

if ([string]::IsNullOrWhiteSpace($Publisher)) {
    $Publisher = "CN=87B3C267-8985-4CA9-B2A8-54EFF3C074C7"
}

if ([string]::IsNullOrWhiteSpace($DisplayName)) {
    $DisplayName = "DualClip"
}

if ([string]::IsNullOrWhiteSpace($PublisherDisplayName)) {
    $PublisherDisplayName = "Rylogix"
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\msix\$normalizedVersion"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot $OutputRoot
}

$packageArchitecture = Get-PackageArchitecture -Rid $RuntimeIdentifier
$projectPath = Join-Path $repoRoot "src\DualClip.App\DualClip.App.csproj"
$sourceManifestPath = Join-Path $repoRoot "packaging\Identity\AppxManifest.xml"
$sourceAppManifestPath = Join-Path $repoRoot "src\DualClip.App\app.manifest"
$tools = Ensure-BuildTools -RepoRoot $repoRoot

if (Test-Path $OutputRoot) {
    Remove-Item -LiteralPath $OutputRoot -Recurse -Force
}

$publishDir = Join-Path $OutputRoot "publish"
$stagingRoot = Join-Path $OutputRoot "staging"
$assetsRoot = Join-Path $stagingRoot "Assets"
$manifestWorkRoot = Join-Path $OutputRoot "manifests"
$generatedManifestPath = Join-Path $manifestWorkRoot "AppxManifest.xml"
$generatedAppManifestPath = Join-Path $manifestWorkRoot "app.manifest"
$msixPath = Join-Path $OutputRoot "DualClip-$normalizedVersion-$packageArchitecture.msix"
$msixUploadPath = Join-Path $OutputRoot "DualClip-$normalizedVersion-$packageArchitecture.msixupload"

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $assetsRoot -Force | Out-Null
New-Item -ItemType Directory -Path $manifestWorkRoot -Force | Out-Null

$appManifestContent = Get-Content -LiteralPath $sourceAppManifestPath -Raw
$appManifestContent = Set-RegexValue -Content $appManifestContent -Pattern '(packageName=")([^"]+)(")' -Replacement ('${1}' + $PackageName + '${3}')
$appManifestContent = Set-RegexValue -Content $appManifestContent -Pattern '(publisher=")([^"]+)(")' -Replacement ('${1}' + $Publisher + '${3}')
[IO.File]::WriteAllText($generatedAppManifestPath, $appManifestContent, [System.Text.UTF8Encoding]::new($false))

Write-Host "Publishing MSIX app payload for v$normalizedVersion..."
dotnet publish $projectPath `
  -c $Configuration `
  -r $RuntimeIdentifier `
  --self-contained true `
  -p:PublishSingleFile=false `
  -p:DebugSymbols=false `
  -p:DebugType=None `
  -p:Version=$normalizedVersion `
  -p:ApplicationManifest=$generatedAppManifestPath `
  --nologo `
  -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

Copy-Item -Path (Join-Path $repoRoot "packaging\Identity\Assets\*") -Destination $assetsRoot -Force
Copy-Item -Path (Join-Path $publishDir "*") -Destination $stagingRoot -Recurse -Force

$manifestContent = Get-Content -LiteralPath $sourceManifestPath -Raw
$manifestContent = Set-RegexValue -Content $manifestContent -Pattern '(<Identity\b[^>]*\bName=")([^"]+)(")' -Replacement ('${1}' + $PackageName + '${3}')
$manifestContent = Set-RegexValue -Content $manifestContent -Pattern '(<Identity\b[^>]*\bPublisher=")([^"]+)(")' -Replacement ('${1}' + $Publisher + '${3}')
$manifestContent = Set-RegexValue -Content $manifestContent -Pattern '(<Identity\b[^>]*\bVersion=")([^"]+)(")' -Replacement ('${1}' + $appxVersion + '${3}')
$manifestContent = Set-RegexValue -Content $manifestContent -Pattern '(<Identity\b[^>]*\bProcessorArchitecture=")([^"]+)(")' -Replacement ('${1}' + $packageArchitecture + '${3}')
$manifestContent = Set-RegexValue -Content $manifestContent -Pattern '<DisplayName>[^<]+</DisplayName>' -Replacement ("<DisplayName>$DisplayName</DisplayName>")
$manifestContent = Set-RegexValue -Content $manifestContent -Pattern '<PublisherDisplayName>[^<]+</PublisherDisplayName>' -Replacement ("<PublisherDisplayName>$PublisherDisplayName</PublisherDisplayName>")
$manifestContent = Set-RegexValue -Content $manifestContent -Pattern '<Description>[^<]+</Description>' -Replacement ("<Description>$Description</Description>")
[IO.File]::WriteAllText($generatedManifestPath, $manifestContent, [System.Text.UTF8Encoding]::new($false))

Copy-Item -LiteralPath $generatedManifestPath -Destination (Join-Path $stagingRoot "AppxManifest.xml") -Force

if (Test-Path $msixPath) {
    Remove-Item -LiteralPath $msixPath -Force
}

Write-Host "Packing MSIX..."
& $tools.MakeAppx pack /o /d $stagingRoot /p $msixPath | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "makeappx.exe failed with exit code $LASTEXITCODE."
}

$resolvedCertificatePath = $CertificatePath
if (-not [string]::IsNullOrWhiteSpace($CertificateBase64)) {
    $resolvedCertificatePath = New-TemporaryCertificateFile -ArtifactsRoot $OutputRoot -Base64Value $CertificateBase64
}
elseif (-not [string]::IsNullOrWhiteSpace($resolvedCertificatePath) -and -not [System.IO.Path]::IsPathRooted($resolvedCertificatePath)) {
    $resolvedCertificatePath = Join-Path $repoRoot $resolvedCertificatePath
}

$isSignedPackage = $false
if (-not [string]::IsNullOrWhiteSpace($resolvedCertificatePath) -and (Test-Path $resolvedCertificatePath)) {
    if ([string]::IsNullOrWhiteSpace($CertificatePassword)) {
        throw "A certificate password is required when signing the MSIX package."
    }

    Write-Host "Signing MSIX..."
    & $tools.SignTool sign /fd SHA256 /f $resolvedCertificatePath /p $CertificatePassword $msixPath | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "signtool.exe failed with exit code $LASTEXITCODE."
    }

    $isSignedPackage = $true
}

if (Test-Path $msixUploadPath) {
    Remove-Item -LiteralPath $msixUploadPath -Force
}

$temporaryZipPath = "$msixUploadPath.zip"
if (Test-Path $temporaryZipPath) {
    Remove-Item -LiteralPath $temporaryZipPath -Force
}

Compress-Archive -LiteralPath $msixPath -DestinationPath $temporaryZipPath -Force
Move-Item -LiteralPath $temporaryZipPath -Destination $msixUploadPath -Force

Write-Host ""
Write-Host "MSIX assets created:"
Write-Host "  MSIX:       $msixPath"
Write-Host "  MSIXUpload: $msixUploadPath"
Write-Host "  Signed:     $isSignedPackage"

[pscustomobject]@{
    Version = $normalizedVersion
    PackageName = $PackageName
    Publisher = $Publisher
    Architecture = $packageArchitecture
    MsixPath = $msixPath
    MsixUploadPath = $msixUploadPath
    IsSigned = $isSignedPackage
}
