[CmdletBinding()]
param(
    [string]$Version,

    [ValidateSet('patch', 'minor', 'major')]
    [string]$Bump = 'patch',

    [string]$Remote = 'origin',

    [switch]$SkipBuild,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Invoke-Tool {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [switch]$AllowFailure
    )

    $stdoutPath = [System.IO.Path]::GetTempFileName()
    $stderrPath = [System.IO.Path]::GetTempFileName()

    $encodedArguments = $Arguments | ForEach-Object {
        if ($_ -match '[\s"]') {
            '"' + ($_ -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
        }
        else {
            $_
        }
    }

    try {
        $process = Start-Process `
            -FilePath $FilePath `
            -ArgumentList ($encodedArguments -join ' ') `
            -NoNewWindow `
            -Wait `
            -PassThru `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath

        $stdout = if ((Get-Item -LiteralPath $stdoutPath).Length -gt 0) {
            @(Get-Content -LiteralPath $stdoutPath)
        }
        else {
            @()
        }

        $stderr = if ((Get-Item -LiteralPath $stderrPath).Length -gt 0) {
            @(Get-Content -LiteralPath $stderrPath)
        }
        else {
            @()
        }
    }
    finally {
        Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    }

    $output = @($stdout + $stderr)
    $exitCode = $process.ExitCode

    if (-not $AllowFailure -and $exitCode -ne 0) {
        $outputText = if ($output) { ($output | Out-String).Trim() } else { '<no output>' }
        throw "Command failed: $FilePath $($Arguments -join ' ')`n$outputText"
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = @($output)
    }
}

function Invoke-ToolStreaming {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        throw "Command failed: $FilePath $($Arguments -join ' ')"
    }
}

function Normalize-StableVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $normalized = $Value.Trim()
    if ($normalized.StartsWith('v')) {
        $normalized = $normalized.Substring(1)
    }

    if ($normalized -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version '$Value' is not a supported release version. Use a stable semantic version like 0.2.0."
    }

    return $normalized
}

function Get-NextStableVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CurrentVersion,

        [Parameter(Mandatory = $true)]
        [ValidateSet('patch', 'minor', 'major')]
        [string]$BumpType
    )

    $parts = Normalize-StableVersion $CurrentVersion
    $major, $minor, $patch = $parts.Split('.') | ForEach-Object { [int]$_ }

    switch ($BumpType) {
        'major' {
            $major++
            $minor = 0
            $patch = 0
        }
        'minor' {
            $minor++
            $patch = 0
        }
        default {
            $patch++
        }
    }

    return "$major.$minor.$patch"
}

function Update-FileVersionString {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Pattern,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Replacement
    )

    $content = Get-Content -LiteralPath $Path -Raw
    $match = [regex]::Match($content, $Pattern)

    if (-not $match.Success) {
        throw "Could not find a version token in '$Path'."
    }

    $updated = [regex]::Replace($content, $Pattern, $Replacement, 1)
    Set-Content -LiteralPath $Path -Value $updated -NoNewline
}

$git = Get-Command git -ErrorAction SilentlyContinue
if (-not $git) {
    throw "Git is required to create the release commit and tag."
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw ".NET SDK is required to validate the release build."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$projectPath = Join-Path $repoRoot 'src\DualClip.App\DualClip.App.csproj'
$manifestPath = Join-Path $repoRoot 'packaging\Identity\AppxManifest.xml'
$solutionPath = Join-Path $repoRoot 'DualClip.sln'

$projectContent = Get-Content -LiteralPath $projectPath -Raw
$projectVersionMatch = [regex]::Match($projectContent, '<Version>(?<version>[^<]+)</Version>')
if (-not $projectVersionMatch.Success) {
    throw "Could not find the project version in '$projectPath'."
}

$currentVersion = $projectVersionMatch.Groups['version'].Value.Trim()
$targetVersion = if ($PSBoundParameters.ContainsKey('Version')) {
    Normalize-StableVersion $Version
}
else {
    Get-NextStableVersion -CurrentVersion $currentVersion -BumpType $Bump
}

$versionAlreadySet = $targetVersion -eq $currentVersion

$tag = "v$targetVersion"
$appxVersion = "$targetVersion.0"

$branchResult = Invoke-Tool -FilePath $git.Source -Arguments @('branch', '--show-current')
$branch = (($branchResult.Output | Select-Object -First 1) -as [string]).Trim()
if ([string]::IsNullOrWhiteSpace($branch)) {
    throw 'Releases must be created from a named branch. Detached HEAD is not supported.'
}

Write-Host "Fetching '$Remote' and tags..."
$null = Invoke-Tool -FilePath $git.Source -Arguments @('fetch', $Remote, $branch, '--tags')

$remoteBranchRef = "refs/remotes/$Remote/$branch"
$remoteBranchCheck = Invoke-Tool -FilePath $git.Source -Arguments @('show-ref', '--verify', '--quiet', $remoteBranchRef) -AllowFailure
$remoteBranchExists = $remoteBranchCheck.ExitCode -eq 0

$aheadCount = 0
$behindCount = 0
if ($remoteBranchExists) {
    $countResult = Invoke-Tool -FilePath $git.Source -Arguments @('rev-list', '--left-right', '--count', "HEAD...$Remote/$branch")
    $countLine = (($countResult.Output | Select-Object -First 1) -as [string]).Trim()
    $counts = $countLine -split '\s+'

    if ($counts.Count -ne 2) {
        throw "Could not determine branch status from '$countLine'."
    }

    $aheadCount = [int]$counts[0]
    $behindCount = [int]$counts[1]

    if ($behindCount -gt 0) {
        throw "Local branch '$branch' is behind '$Remote/$branch' by $behindCount commit(s). Pull or rebase before releasing."
    }
}

$tagCheck = Invoke-Tool -FilePath $git.Source -Arguments @('rev-parse', '--verify', '--quiet', $tag) -AllowFailure
if ($tagCheck.ExitCode -eq 0) {
    throw "Tag '$tag' already exists locally or was fetched from '$Remote'. Choose a different version."
}

Write-Host ""
Write-Host "Release plan"
Write-Host "  Current version: $currentVersion"
Write-Host "  New version:     $targetVersion"
Write-Host "  Appx version:    $appxVersion"
Write-Host "  Branch:          $branch"
Write-Host "  Remote:          $Remote"
Write-Host "  Tag:             $tag"
if ($remoteBranchExists) {
    Write-Host "  Ahead of remote: $aheadCount commit(s)"
}
else {
    Write-Host "  Ahead of remote: remote branch does not exist yet"
}
if ($versionAlreadySet) {
    Write-Host "  Version files:   already set to target version"
}

if ($DryRun) {
    Write-Host ""
    Write-Host "Dry run only. No files were changed, committed, tagged, or pushed."
    return
}

Write-Host ""
if ($versionAlreadySet) {
    Write-Host "Checked-in versions already match $targetVersion."
}
else {
    Write-Host "Updating checked-in versions..."
}

if (-not $versionAlreadySet) {
    Update-FileVersionString -Path $projectPath -Pattern '<Version>[^<]+</Version>' -Replacement {
        "<Version>$targetVersion</Version>"
    }

    Update-FileVersionString -Path $manifestPath -Pattern '(<Identity\b[^>]*\bVersion=")([^"]+)(")' -Replacement {
        param($match)
        $match.Groups[1].Value + $appxVersion + $match.Groups[3].Value
    }
}

if (-not $SkipBuild) {
    Write-Host ""
    Write-Host "Running release build validation..."
    Write-Host "Build output will stream below."
    Invoke-ToolStreaming -FilePath $dotnet.Source -Arguments @('build', $solutionPath, '-c', 'Release', '--nologo')
}

$commitMessage = "release: $tag"

Write-Host ""
Write-Host "Creating release commit..."
$null = Invoke-Tool -FilePath $git.Source -Arguments @('add', '-A')
$null = Invoke-Tool -FilePath $git.Source -Arguments @('commit', '-m', $commitMessage)

Write-Host ""
Write-Host "Creating annotated tag..."
$null = Invoke-Tool -FilePath $git.Source -Arguments @('tag', '-a', $tag, '-m', "Release $tag")

Write-Host ""
Write-Host "Pushing branch '$branch'..."
$null = Invoke-Tool -FilePath $git.Source -Arguments @('push', $Remote, $branch)

Write-Host ""
Write-Host "Pushing tag '$tag'..."
$null = Invoke-Tool -FilePath $git.Source -Arguments @('push', $Remote, $tag)

Write-Host ""
Write-Host "Release pushed successfully."
Write-Host "GitHub Actions should now build and publish the $tag release."
