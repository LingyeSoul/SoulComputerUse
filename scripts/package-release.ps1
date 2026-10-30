#Requires -Version 5.1

<#
.SYNOPSIS
Builds the self-contained Windows x64 release archives and their checksums.

.DESCRIPTION
Publishes the host, CLI, and MCP entry points into one staging directory, verifies that the
host's child-session agent and elevation helper are present, writes install-manifest.json
with the SHA-256 of every file in the staging directory, then creates:

  SoulComputerUse-win-x64.zip
  soul-computer-use-skill.zip
  SHA256SUMS.txt

The application archive keeps a version-free name so release links can point at
releases/latest/download; the release tag carries the version. The builds inside the archive
are stamped with <Version>, so the executables report it.

The host publish builds and copies the agent and helper because the host project references
them as content. The CLI and MCP publishes must target the same directory so all five
executables live next to each other.

.PARAMETER Version
SemVer version without a leading "v", for example "1.2.3" or "1.2.3-rc.1".

.PARAMETER OutputDirectory
Directory that receives the archives and checksum file. It may already exist.

.EXAMPLE
pwsh -NoProfile -File .\scripts\package-release.ps1 -Version 1.2.3 -OutputDirectory artifacts\release
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputFull = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputFull -Force | Out-Null

$staging = Join-Path ([System.IO.Path]::GetTempPath()) ('SoulComputerUse-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null

$projects = @(
    'src\SoulComputerUse.Host\SoulComputerUse.Host.csproj',
    'src\SoulComputerUse.Cli\SoulComputerUse.Cli.csproj',
    'src\SoulComputerUse.Mcp\SoulComputerUse.Mcp.csproj'
)

$requiredExecutables = @(
    'SoulComputerUse.Host.exe',
    'SoulComputerUse.ChildSessionsHelper.exe',
    'SoulComputerUse.SessionAgent.exe',
    'SoulComputerUse.Cli.exe',
    'SoulComputerUse.Mcp.exe'
)

try {
    foreach ($project in $projects) {
        $projectPath = Join-Path $repositoryRoot $project
        Write-Host "Publishing $project"
        dotnet publish $projectPath `
            -c Release `
            -r win-x64 `
            --self-contained true `
            -p:PublishReadyToRun=false `
            -p:DebugType=None `
            -p:DebugSymbols=false `
            -p:Version=$Version `
            -o $staging
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed for '$project' with exit code $LASTEXITCODE."
        }
    }

    $licensePath = Join-Path $repositoryRoot 'LICENSE'
    if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
        throw "The repository LICENSE file is missing; the release must ship it."
    }

    Copy-Item -LiteralPath $licensePath -Destination (Join-Path $staging 'LICENSE') -Force

    # The installer verifies this manifest before and after every copy, and it refuses any file the
    # manifest does not list, so every payload file must carry a hash here. Keys are '/'-separated
    # paths relative to the archive root; the manifest itself is written afterwards and cannot list
    # itself. Sorting keeps two runs over identical bytes byte-identical.
    $manifestName = 'install-manifest.json'
    $manifestFiles = [ordered]@{}
    $payloadFiles = Get-ChildItem -LiteralPath $staging -Recurse -File | Sort-Object -Property FullName
    foreach ($payloadFile in $payloadFiles) {
        $relativeKey = [System.IO.Path]::GetRelativePath($staging, $payloadFile.FullName).Replace('\', '/')
        $manifestFiles[$relativeKey] = (Get-FileHash -LiteralPath $payloadFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    # The full enumeration above is the real coverage; this lower bound keeps a release that lost an
    # entry point from ever reaching the manifest, because the installer refuses a key set without them.
    foreach ($executable in $requiredExecutables) {
        if (-not $manifestFiles.Contains($executable)) {
            throw "The staging directory is missing '$executable'. Publish the host before the CLI and MCP."
        }
    }

    # manifestVersion 2 keys files by '/'-separated path relative to the archive root and covers every
    # file; version 1 listed only the five executables by bare file name.
    $manifest = [ordered]@{
        manifestVersion = 2
        productVersion = $Version
        algorithm = 'SHA-256'
        files = $manifestFiles
    }

    $manifestPath = Join-Path $staging $manifestName
    $manifestJson = $manifest | ConvertTo-Json -Depth 3
    [System.IO.File]::WriteAllText($manifestPath, $manifestJson, (New-Object System.Text.UTF8Encoding($false)))

    $appArchive = Join-Path $outputFull 'SoulComputerUse-win-x64.zip'
    if (Test-Path -LiteralPath $appArchive) {
        Remove-Item -LiteralPath $appArchive -Force
    }

    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $appArchive -CompressionLevel Optimal

    $skillDirectory = Join-Path $repositoryRoot 'skills\soul-computer-use'
    if (-not (Test-Path -LiteralPath (Join-Path $skillDirectory 'SKILL.md') -PathType Leaf)) {
        throw "The skill directory is missing SKILL.md: $skillDirectory"
    }

    $skillArchive = Join-Path $outputFull 'soul-computer-use-skill.zip'
    if (Test-Path -LiteralPath $skillArchive) {
        Remove-Item -LiteralPath $skillArchive -Force
    }

    Compress-Archive -Path (Join-Path $skillDirectory '*') -DestinationPath $skillArchive -CompressionLevel Optimal

    $checksumLines = foreach ($archive in @($appArchive, $skillArchive)) {
        $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $([System.IO.Path]::GetFileName($archive))"
    }

    $checksumPath = Join-Path $outputFull 'SHA256SUMS.txt'
    Set-Content -LiteralPath $checksumPath -Value $checksumLines -Encoding ascii

    [pscustomobject]@{
        version = $Version
        appArchive = $appArchive
        skillArchive = $skillArchive
        checksums = $checksumPath
        # Archive-relative path; the manifest ships inside the application archive.
        manifest = $manifestName
    } | ConvertTo-Json -Depth 3
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}
