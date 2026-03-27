[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

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
$projectPath = Join-Path $repoRoot "src\DualClip.App\DualClip.App.csproj"
$repository = "Rylogix/DualClip"

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

Write-Host ""
Write-Host "Release asset created:"
Write-Host "  $assetPath"
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

& $gh.Source release view $tag --repo $repository *> $null
$releaseExists = $LASTEXITCODE -eq 0

if ($releaseExists) {
    & $gh.Source release upload $tag $assetPath --repo $repository --clobber
    if ($LASTEXITCODE -ne 0) {
        throw "gh release upload failed."
    }
}
else {
    $createArgs = @("release", "create", $tag, $assetPath, "--repo", $repository, "--title", "DualClip $tag")

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
