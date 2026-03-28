[CmdletBinding()]
param(
    [string]$OutputPath,

    [string]$PublishedExePath,

    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$logDir = Join-Path $repoRoot 'artifacts\logs'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$logTimestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logPath = Join-Path $logDir "Build-NewExe-$logTimestamp.log"
$transcriptStarted = $false

try {
    Start-Transcript -Path $logPath -Force | Out-Null
    $transcriptStarted = $true
}
catch {
    Write-Warning "Failed to start build transcript at '$logPath'. $_"
}

trap {
    if ($transcriptStarted) {
        try {
            Stop-Transcript | Out-Null
        }
        catch {
        }
    }

    throw
}

Write-Host "Build log: $logPath"

$projectPath = Join-Path $repoRoot 'src\DualClip.App\DualClip.App.csproj'
$publishDir = Join-Path $repoRoot 'publish\DualClip.SingleFile'
$stagingPublishDir = Join-Path $repoRoot 'artifacts\publish-staging\DualClip.SingleFile'

if (-not $PSBoundParameters.ContainsKey('PublishedExePath')) {
    $PublishedExePath = Join-Path $publishDir 'DualClip.App.exe'
}
elseif (-not [System.IO.Path]::IsPathRooted($PublishedExePath)) {
    $PublishedExePath = Join-Path $repoRoot $PublishedExePath
}

if (-not $PSBoundParameters.ContainsKey('OutputPath')) {
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputPath = Join-Path $repoRoot "artifacts\local-builds\$timestamp\DualClip.App.exe"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $repoRoot $OutputPath
}

$outputDirectory = Split-Path -Parent $OutputPath
if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
    throw "Could not determine an output directory from '$OutputPath'."
}

if (-not $SkipPublish) {
    Write-Host "Closing any running DualClip instance..."
    Get-Process -Name 'DualClip.App' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1

    Write-Host "Resetting publish staging directory '$stagingPublishDir'..."
    Remove-Item -LiteralPath $stagingPublishDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $stagingPublishDir -Force | Out-Null

    Write-Host "Publishing current source to staging..."
    dotnet publish $projectPath `
      -c Release `
      -r win-x64 `
      --self-contained true `
      -p:PublishSingleFile=true `
      -p:EnableCompressionInSingleFile=true `
      -p:IncludeNativeLibrariesForSelfExtract=true `
      -p:DebugSymbols=false `
      -p:DebugType=None `
      --nologo `
      -o $stagingPublishDir

    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet publish failed.'
    }

    Write-Host "Refreshing publish output at '$publishDir'..."
    Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
    Copy-Item -Path (Join-Path $stagingPublishDir '*') -Destination $publishDir -Recurse -Force
}

if (-not (Test-Path -LiteralPath $PublishedExePath -PathType Leaf)) {
    throw "The published exe was not found at '$PublishedExePath'."
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
Copy-Item -LiteralPath $PublishedExePath -Destination $OutputPath -Force

$sourceFile = Get-Item -LiteralPath $PublishedExePath
$outputFile = Get-Item -LiteralPath $OutputPath

Write-Host ''
Write-Host 'New exe created:'
Write-Host "  Source:      $($sourceFile.FullName)"
Write-Host "  Destination: $($outputFile.FullName)"
Write-Host "  Size:        $([Math]::Round($outputFile.Length / 1MB, 2)) MB"
Write-Host "  Log:         $logPath"

if ($transcriptStarted) {
    Stop-Transcript | Out-Null
}
