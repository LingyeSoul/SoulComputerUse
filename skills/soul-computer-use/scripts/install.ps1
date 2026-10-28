#Requires -Version 5.1

<#
.SYNOPSIS
Downloads, verifies, and installs a SoulComputerUse release for the current user.

.DESCRIPTION
The installer resolves a GitHub Release by tag, downloads the Windows x64 archive and
SHA256SUMS.txt, verifies the archive hash, expands it into the destination, unblocks the
files, and runs the CLI self-check. It never edits PATH, the registry, or an agent config.

.PARAMETER Repository
GitHub repository in owner/name form, for example "LingyeSoul/SoulComputerUse".

.PARAMETER Version
"latest" or a release tag such as "v1.2.3". A bare "1.2.3" is normalized to "v1.2.3".

.PARAMETER Destination
Empty or nonexistent directory that receives the application files.

.EXAMPLE
pwsh -NoProfile -File .\scripts\install.ps1 -Repository LingyeSoul/SoulComputerUse -Version latest -Destination "$env:LOCALAPPDATA\SoulComputerUse\app"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$Destination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 6) {
    # Windows PowerShell 5.1 can still default to TLS 1.0 on older images; the GitHub API refuses it.
    [Net.ServicePointManager]::SecurityProtocol =
        [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    if (-not [string]::IsNullOrEmpty($env:GITHUB_TOKEN)) {
        # 5.1 forwards the Authorization header across GitHub's asset redirect, which the storage host
        # rejects. For a public repository, clear GITHUB_TOKEN; for a private one, run with pwsh.
        throw "Windows PowerShell 5.1 cannot download GitHub release assets with GITHUB_TOKEN set: the asset redirect carries the Authorization header to the storage host, which refuses it. Clear GITHUB_TOKEN for a public repository, or run this script with pwsh (PowerShell 7) for a private one."
    }
}

$archiveName = 'SoulComputerUse-win-x64.zip'
$checksumName = 'SHA256SUMS.txt'
$requiredExecutables = @(
    'SoulComputerUse.Host.exe',
    'SoulComputerUse.ChildSessionsHelper.exe',
    'SoulComputerUse.SessionAgent.exe',
    'SoulComputerUse.Cli.exe',
    'SoulComputerUse.Mcp.exe'
)

function Get-GitHubHeaders {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Accept
    )

    $headers = @{
        'User-Agent' = 'SoulComputerUse-Installer'
        'Accept' = $Accept
    }

    if (-not [string]::IsNullOrEmpty($env:GITHUB_TOKEN)) {
        $headers['Authorization'] = "Bearer $($env:GITHUB_TOKEN)"
    }

    return $headers
}

function Get-Release {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Repository,

        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    if ($Version -eq 'latest') {
        $uri = "https://api.github.com/repos/$Repository/releases/latest"
    }
    else {
        $tag = $Version
        if (-not $tag.StartsWith('v')) {
            $tag = "v$tag"
        }

        $uri = "https://api.github.com/repos/$Repository/releases/tags/$tag"
    }

    $release = Invoke-RestMethod -Uri $uri -Headers (Get-GitHubHeaders -Accept 'application/vnd.github+json')
    return $release
}

function Get-ReleaseAsset {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        $Release,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $asset = $Release.assets | Where-Object { $_.name -eq $Name } | Select-Object -First 1
    if ($null -eq $asset) {
        throw "Release $($Release.tag_name) does not contain the asset '$Name'."
    }

    return $asset
}

$release = Get-Release -Repository $Repository -Version $Version
$archiveAsset = Get-ReleaseAsset -Release $release -Name $archiveName
$checksumAsset = Get-ReleaseAsset -Release $release -Name $checksumName

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('SoulComputerUse-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null

try {
    $archivePath = Join-Path $tempRoot $archiveName
    $checksumPath = Join-Path $tempRoot $checksumName

    $binaryHeaders = Get-GitHubHeaders -Accept 'application/octet-stream'
    Invoke-WebRequest -Uri $archiveAsset.url -Headers $binaryHeaders -OutFile $archivePath -UseBasicParsing
    Invoke-WebRequest -Uri $checksumAsset.url -Headers $binaryHeaders -OutFile $checksumPath -UseBasicParsing

    $escapedName = [regex]::Escape($archiveName)
    $checksumLine = Get-Content -LiteralPath $checksumPath |
        Where-Object { $_ -match "^([0-9a-fA-F]{64})\s+$escapedName$" } |
        Select-Object -First 1
    if ($null -eq $checksumLine) {
        throw "'$checksumName' does not contain a SHA-256 entry for '$archiveName'; refusing to install unverified bytes."
    }

    $expectedHash = [regex]::Match($checksumLine, '^([0-9a-fA-F]{64})').Groups[1].Value.ToLowerInvariant()
    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "SHA-256 mismatch for '$archiveName'. Expected $expectedHash, got $actualHash."
    }

    $extractRoot = Join-Path $tempRoot 'extract'
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot -Force

    foreach ($executable in $requiredExecutables) {
        $executablePath = Join-Path $extractRoot $executable
        if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
            throw "The release archive is missing '$executable' at its root; the archive layout is not installable."
        }
    }

    $destinationFull = [System.IO.Path]::GetFullPath($Destination)
    if (Test-Path -LiteralPath $destinationFull) {
        if (-not (Test-Path -LiteralPath $destinationFull -PathType Container)) {
            throw "Destination exists and is not a directory: $destinationFull"
        }

        $existing = @(Get-ChildItem -LiteralPath $destinationFull -Force)
        if ($existing.Count -gt 0) {
            throw "Destination is not empty: $destinationFull. Remove it or choose an empty directory."
        }
    }
    else {
        New-Item -ItemType Directory -Path $destinationFull -Force | Out-Null
    }

    Copy-Item -Path (Join-Path $extractRoot '*') -Destination $destinationFull -Recurse -Force
    Get-ChildItem -LiteralPath $destinationFull -Recurse -File | Unblock-File

    $cliPath = Join-Path $destinationFull 'SoulComputerUse.Cli.exe'
    & $cliPath --help | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "The installed CLI failed its self-check with exit code $LASTEXITCODE."
    }

    [pscustomobject]@{
        version = $release.tag_name
        installPath = $destinationFull
        host = Join-Path $destinationFull 'SoulComputerUse.Host.exe'
        cli = $cliPath
        mcp = Join-Path $destinationFull 'SoulComputerUse.Mcp.exe'
    } | ConvertTo-Json -Depth 3
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
