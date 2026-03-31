[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string[]]$RuntimeIdentifiers = @("win-x64", "win-arm64"),

    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $repoRoot

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\msix\$Version"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot $OutputRoot
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

$results = @()
foreach ($runtimeIdentifier in ($RuntimeIdentifiers | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
    $architectureRoot = Join-Path $OutputRoot ($runtimeIdentifier -replace '^win-', '')
    $scriptOutput = @(& (Join-Path $PSScriptRoot "Build-DualClipMsix.ps1") `
        -Version $Version `
        -Configuration $Configuration `
        -RuntimeIdentifier $runtimeIdentifier `
        -OutputRoot $architectureRoot)

    $result = $scriptOutput |
        Where-Object { $_ -and $_.PSObject.Properties.Match('MsixPath').Count -gt 0 } |
        Select-Object -Last 1

    if (-not $result) {
        throw "MSIX build for '$runtimeIdentifier' did not return a package result."
    }

    $results += $result
}

$guidePath = Join-Path $OutputRoot "README.txt"
$guideLines = @(
    "DualClip Store Submission Assets",
    "Version: $Version",
    "",
    "Use these .msixupload files for Partner Center:",
    ""
)

foreach ($result in ($results | Sort-Object Architecture)) {
    $relativeUploadPath = Get-RelativePathCompat -BasePath $OutputRoot -TargetPath $result.MsixUploadPath
    $guideLines += " - [$($result.Architecture)] $relativeUploadPath"
}

[System.IO.File]::WriteAllLines($guidePath, $guideLines)

Write-Host ""
Write-Host "Store packaging complete:"
foreach ($result in $results) {
    Write-Host "  [$($result.Architecture)] $($result.MsixUploadPath)"
}
Write-Host "  [guide] $guidePath"

$results
