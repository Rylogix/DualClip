[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [switch]$IncludeMsix,

    [switch]$Upload,

    [string]$NotesFile,

    [string[]]$MsixRuntimeIdentifiers = @('win-x64', 'win-arm64')
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$normalizedVersion = $Version.Trim()
if ($normalizedVersion.StartsWith('v')) {
    $normalizedVersion = $normalizedVersion.Substring(1)
}

if ($normalizedVersion -notmatch '^\d+\.\d+\.\d+([\-+][0-9A-Za-z\.-]+)?$') {
    throw "Version '$Version' is not a supported semantic version. Use values like 0.2.0 or v0.2.0."
}

$tag = "v$normalizedVersion"
$releaseRoot = Join-Path $repoRoot "artifacts\github-release\$normalizedVersion"
$portableRoot = Join-Path $releaseRoot "portable"
$publishDir = Join-Path $portableRoot "publish"
$assetName = "DualClip.App-$normalizedVersion.exe"
$assetPath = Join-Path $portableRoot $assetName
$storeSubmissionRoot = Join-Path $releaseRoot "store-submission"
$msixRoot = Join-Path $storeSubmissionRoot "packages"
$storeSubmissionGuidePath = Join-Path $storeSubmissionRoot "README.txt"
$projectPath = Join-Path $repoRoot "src\DualClip.App\DualClip.App.csproj"
$repository = "Rylogix/DualClip"
$msixArtifacts = @()

function Invoke-MsixBuild {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RuntimeIdentifier,

        [Parameter(Mandatory = $true)]
        [string]$OutputRoot
    )

    $scriptOutput = @(& (Join-Path $PSScriptRoot "Build-DualClipMsix.ps1") `
        -Version $normalizedVersion `
        -Configuration Release `
        -RuntimeIdentifier $RuntimeIdentifier `
        -OutputRoot $OutputRoot)

    $result = $scriptOutput |
        Where-Object { $_ -and $_.PSObject.Properties.Match('MsixPath').Count -gt 0 } |
        Select-Object -Last 1

    if (-not $result) {
        throw "MSIX build for '$RuntimeIdentifier' did not return a package result."
    }

    return $result
}

function Get-RelativePathCompat {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BasePath,

        [Parameter(Mandatory = $true)]
        [string]$TargetPath
    )

    $baseDirectory = [System.IO.Path]::GetFullPath($BasePath)
    if (-not $baseDirectory.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $baseDirectory += [System.IO.Path]::DirectorySeparatorChar
    }

    $baseUri = [System.Uri]::new($baseDirectory)
    $targetUri = [System.Uri]::new([System.IO.Path]::GetFullPath($TargetPath))
    return [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace('/', '\')
}

function Write-StoreSubmissionGuide {
    param(
        [Parameter(Mandatory = $true)]
        [string]$GuidePath,

        [Parameter(Mandatory = $true)]
        [string[]]$ArtifactPaths
    )

    $lines = @(
        "DualClip Store Submission Assets",
        "Version: $normalizedVersion",
        "",
        "Use the .msixupload files below for Partner Center submission:",
        ""
    )

    foreach ($artifactPath in ($ArtifactPaths | Where-Object { $_ -like '*.msixupload' } | Sort-Object)) {
        $relativePath = Get-RelativePathCompat -BasePath $releaseRoot -TargetPath $artifactPath
        $lines += " - $relativePath"
    }

    $lines += @(
        "",
        "Portable asset:",
        " - " + (Get-RelativePathCompat -BasePath $releaseRoot -TargetPath $assetPath)
    )

    [System.IO.File]::WriteAllLines($GuidePath, $lines)
}

Write-Host "Preparing GitHub release asset for $tag..."

if (Test-Path $releaseRoot) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $storeSubmissionRoot -Force | Out-Null

dotnet publish $projectPath `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugSymbols=false `
  -p:DebugType=None `
  -p:Version=$normalizedVersion `
  --nologo `
  -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

Copy-Item -LiteralPath (Join-Path $publishDir "DualClip.App.exe") -Destination $assetPath -Force

if ($IncludeMsix) {
    Write-Host ""
    Write-Host "Building MSIX release assets..."
    foreach ($runtimeIdentifier in ($MsixRuntimeIdentifiers | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
        $architectureRoot = Join-Path $msixRoot ($runtimeIdentifier -replace '^win-', '')
        Write-Host "  -> $runtimeIdentifier"
        $msixResult = Invoke-MsixBuild -RuntimeIdentifier $runtimeIdentifier -OutputRoot $architectureRoot

        $msixArtifacts += @($msixResult.MsixPath, $msixResult.MsixUploadPath) | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_)
        }
    }

    Write-StoreSubmissionGuide -GuidePath $storeSubmissionGuidePath -ArtifactPaths $msixArtifacts
}

Write-Host ""
Write-Host "Release asset created:"
Write-Host "  $assetPath"
if ($IncludeMsix) {
    Write-Host "  $storeSubmissionGuidePath"
    foreach ($msixArtifact in $msixArtifacts) {
        Write-Host "  $msixArtifact"
    }
}
Write-Host ""
Write-Host "GitHub tag:"
Write-Host "  $tag"

if (-not $Upload) {
    Write-Host ""
    Write-Host "Upload skipped. Run with -Upload to create or update the GitHub release through gh."
    return
}

$gh = Get-Command gh -ErrorAction SilentlyContinue
if (-not $gh) {
    throw "GitHub CLI 'gh' is required when using -Upload."
}

Write-Host ""
Write-Host "Uploading $assetPath to $repository..."

$assetsToUpload = @($assetPath)
if ($IncludeMsix) {
    $assetsToUpload += $msixArtifacts
}

& $gh.Source release view $tag --repo $repository *> $null
$releaseExists = $LASTEXITCODE -eq 0

if ($releaseExists) {
    if (-not [string]::IsNullOrWhiteSpace($NotesFile)) {
        $resolvedNotesFile = Resolve-Path -LiteralPath $NotesFile
        & $gh.Source release edit $tag --repo $repository --notes-file $resolvedNotesFile.Path --title "DualClip $tag"
        if ($LASTEXITCODE -ne 0) {
            throw "gh release edit failed."
        }
    }

    $uploadArgs = @("release", "upload", $tag) + $assetsToUpload + @("--repo", $repository, "--clobber")
    & $gh.Source @uploadArgs
    if ($LASTEXITCODE -ne 0) {
        throw "gh release upload failed."
    }
}
else {
    $createArgs = @("release", "create", $tag) + $assetsToUpload + @("--repo", $repository, "--title", "DualClip $tag")

    if ([string]::IsNullOrWhiteSpace($NotesFile)) {
        $createArgs += "--generate-notes"
    }
    else {
        $resolvedNotesFile = Resolve-Path -LiteralPath $NotesFile
        $createArgs += @("--notes-file", $resolvedNotesFile.Path)
    }

    & $gh.Source @createArgs
    if ($LASTEXITCODE -ne 0) {
        throw "gh release create failed."
    }
}

Write-Host ""
Write-Host "GitHub release is ready for $tag."
