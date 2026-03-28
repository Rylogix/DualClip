[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [switch]$IncludeMsix,

    [switch]$Upload,

    [string]$NotesFile
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
$publishDir = Join-Path $releaseRoot "publish"
$assetPath = Join-Path $releaseRoot "DualClip.App.exe"
$msixRoot = Join-Path $releaseRoot "msix"
$projectPath = Join-Path $repoRoot "src\DualClip.App\DualClip.App.csproj"
$repository = "Rylogix/DualClip"
$msixPath = $null
$msixUploadPath = $null

Write-Host "Preparing GitHub release asset for $tag..."

if (Test-Path $releaseRoot) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

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
    $msixResult = & (Join-Path $PSScriptRoot "Build-DualClipMsix.ps1") `
        -Version $normalizedVersion `
        -Configuration Release `
        -OutputRoot $msixRoot

    $msixPath = $msixResult.MsixPath
    $msixUploadPath = $msixResult.MsixUploadPath
}

Write-Host ""
Write-Host "Release asset created:"
Write-Host "  $assetPath"
if ($IncludeMsix) {
    Write-Host "  $msixPath"
    Write-Host "  $msixUploadPath"
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
    if (-not [string]::IsNullOrWhiteSpace($msixPath) -and (Test-Path -LiteralPath $msixPath)) {
        $assetsToUpload += $msixPath
    }

    if (-not [string]::IsNullOrWhiteSpace($msixUploadPath) -and (Test-Path -LiteralPath $msixUploadPath)) {
        $assetsToUpload += $msixUploadPath
    }
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
