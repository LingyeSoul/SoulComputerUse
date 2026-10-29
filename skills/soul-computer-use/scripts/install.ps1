#Requires -Version 5.1

<#
.SYNOPSIS
Downloads, verifies, and installs a SoulComputerUse release into Program Files.

.DESCRIPTION
In download mode (the default), the installer resolves a GitHub Release by tag, downloads the
Windows x64 archive and SHA256SUMS.txt, verifies the archive hash, expands it to a temporary
directory, and verifies install-manifest.json against every extracted file. It then starts this
same script elevated with -PayloadDirectory and -Destination; the elevated process re-verifies the
payload, copies it into %ProgramFiles%\SoulComputerUse, verifies the copy against the same
manifest, and runs the CLI self-check. If anything fails after the copy starts, the elevated
process removes the directory it populated so a retry is not blocked. The installer never edits
PATH, the registry, or an agent config.

Run download mode from a normal-privilege PowerShell prompt: downloading and verifying need no
administrator rights, and the script asks for a single UAC prompt only for the copy. Install mode
(-PayloadDirectory) must run elevated and performs no network access. The destination must be
inside %ProgramFiles% and must not be the Program Files root.

.PARAMETER Repository
GitHub repository in owner/name form, for example "LingyeSoul/SoulComputerUse".

.PARAMETER Version
"latest" or a release tag such as "v1.2.3". A bare "1.2.3" is normalized to "v1.2.3".

.PARAMETER PayloadDirectory
Verified release directory produced by the download phase. Install mode only; used by the
elevated child process.

.PARAMETER Destination
Empty or nonexistent directory inside %ProgramFiles%, normally
%ProgramFiles%\SoulComputerUse.

.EXAMPLE
# Run from a normal-privilege PowerShell prompt; the script requests one UAC prompt for the copy.
pwsh -NoProfile -File .\scripts\install.ps1 -Repository LingyeSoul/SoulComputerUse -Version latest -Destination "$env:ProgramFiles\SoulComputerUse"

.EXAMPLE
# Internal phase invoked by the download phase with the already verified payload.
pwsh -NoProfile -File .\scripts\install.ps1 -PayloadDirectory "$env:TEMP\SoulComputerUse-abc\extract" -Destination "$env:ProgramFiles\SoulComputerUse"
#>
[CmdletBinding(DefaultParameterSetName = 'Download')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Download')]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository,

    [Parameter(Mandatory = $true, ParameterSetName = 'Download')]
    [string]$Version,

    [Parameter(Mandatory = $true, ParameterSetName = 'Install')]
    [string]$PayloadDirectory,

    [Parameter(Mandatory = $true)]
    [string]$Destination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The application lives in a machine-wide, administrator-protected directory. The ownership record
# under %LOCALAPPDATA% is per-user state and is unrelated to these files.
$programFilesRoot = [Environment]::GetFolderPath('ProgramFiles')
if ([string]::IsNullOrEmpty($programFilesRoot)) {
    throw "Cannot resolve the Program Files directory from the current environment; the supported layout is %ProgramFiles%\SoulComputerUse."
}

$programFilesRoot = [System.IO.Path]::GetFullPath($programFilesRoot)
$destinationFull = [System.IO.Path]::GetFullPath($Destination)
$programFilesPrefix = $programFilesRoot.TrimEnd('\') + '\'
$destinationIsProgramFilesRoot =
    [string]::Equals($destinationFull.TrimEnd('\'), $programFilesRoot.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)
if (-not $destinationFull.StartsWith($programFilesPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or $destinationIsProgramFilesRoot) {
    throw "Destination must be a directory under %ProgramFiles%, for example '$programFilesRoot\SoulComputerUse'; got '$destinationFull'."
}

$archiveName = 'SoulComputerUse-win-x64.zip'
$checksumName = 'SHA256SUMS.txt'
$manifestName = 'install-manifest.json'
$resultName = 'install-result.json'
$manifestAlgorithm = 'SHA-256'
$supportedManifestVersion = 1
$requiredExecutables = @(
    'SoulComputerUse.Host.exe',
    'SoulComputerUse.ChildSessionsHelper.exe',
    'SoulComputerUse.SessionAgent.exe',
    'SoulComputerUse.Cli.exe',
    'SoulComputerUse.Mcp.exe'
)

$currentPrincipal = New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())
$isElevated = $currentPrincipal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
$shellExecutable = Join-Path $PSHOME $(if ($PSVersionTable.PSVersion.Major -lt 6) { 'powershell.exe' } else { 'pwsh.exe' })

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

function Assert-ManifestFiles {
    <#
    .SYNOPSIS
    Verifies that every file listed in install-manifest.json exists under a root and matches its SHA-256.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        $ManifestFiles,

        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string[]]$RequiredNames,

        [Parameter(Mandatory = $true)]
        [string]$ManifestName
    )

    foreach ($requiredName in $RequiredNames) {
        if ($null -eq $ManifestFiles.PSObject.Properties[$requiredName]) {
            throw "'$ManifestName' does not list '$requiredName'; refusing to install unverified bytes."
        }
    }

    foreach ($fileEntry in $ManifestFiles.PSObject.Properties) {
        $name = $fileEntry.Name
        if ($name.Length -eq 0 -or $name.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $name -eq '.' -or $name -eq '..') {
            throw "'$ManifestName' lists '$name', which is not a plain file name at the archive root; refusing to install unverified bytes."
        }

        $expectedHash = [string]$fileEntry.Value
        if ($expectedHash -notmatch '^[0-9a-fA-F]{64}$') {
            throw "'$ManifestName' does not contain a SHA-256 hash for '$name'; refusing to install unverified bytes."
        }

        $filePath = Join-Path $Root $name
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            throw "'$ManifestName' lists '$name', which is missing from '$Root'; refusing to install unverified bytes."
        }

        $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualHash, $expectedHash, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "SHA-256 mismatch for '$name' in '$Root'. Expected $expectedHash, got $actualHash; refusing to install unverified bytes."
        }
    }
}

function Get-InstallManifest {
    <#
    .SYNOPSIS
    Reads install-manifest.json under a root, validates its header, and verifies every listed file.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$ManifestName,

        [Parameter(Mandatory = $true)]
        [int]$SupportedVersion,

        [Parameter(Mandatory = $true)]
        [string]$Algorithm,

        [Parameter(Mandatory = $true)]
        [string[]]$RequiredNames
    )

    $manifestPath = Join-Path $Root $ManifestName
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "The payload directory '$Root' is missing '$ManifestName'; refusing to install unverified bytes."
    }

    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "'$ManifestName' in '$Root' is not valid JSON: $($_.Exception.Message)"
    }

    if ($null -eq $manifest) {
        throw "'$ManifestName' in '$Root' must contain a JSON object; refusing to install unverified bytes."
    }

    $manifestVersionProperty = $manifest.PSObject.Properties['manifestVersion']
    if ($null -eq $manifestVersionProperty -or
        ($manifestVersionProperty.Value -isnot [int] -and $manifestVersionProperty.Value -isnot [long]) -or
        $manifestVersionProperty.Value -ne $SupportedVersion) {
        throw "'$ManifestName' must declare manifestVersion $SupportedVersion; refusing to install unverified bytes."
    }

    $algorithmProperty = $manifest.PSObject.Properties['algorithm']
    if ($null -eq $algorithmProperty -or $algorithmProperty.Value -isnot [string] -or $algorithmProperty.Value -cne $Algorithm) {
        throw "'$ManifestName' must declare algorithm '$Algorithm'; refusing to install unverified bytes."
    }

    $filesProperty = $manifest.PSObject.Properties['files']
    if ($null -eq $filesProperty -or $null -eq $filesProperty.Value) {
        throw "'$ManifestName' must list the release files under 'files'; refusing to install unverified bytes."
    }

    Assert-ManifestFiles -ManifestFiles $filesProperty.Value -Root $Root -RequiredNames $RequiredNames -ManifestName $ManifestName
    return $manifest
}

function ConvertTo-QuotedArgument {
    <#
    .SYNOPSIS
    Quotes one command-line argument for Start-Process, keeping trailing separators inside the quotes.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    if ($Value.IndexOf('"') -ge 0) {
        throw "Cannot pass an argument that contains a double quote: $Value"
    }

    $trimmed = $Value
    while ($trimmed.Length -gt 3 -and $trimmed.EndsWith('\')) {
        $trimmed = $trimmed.Substring(0, $trimmed.Length - 1)
    }

    return '"' + $trimmed + '"'
}

function Test-ElevationDeclined {
    <#
    .SYNOPSIS
    Detects the Win32 "operation was canceled by the user" error raised when a UAC prompt is dismissed.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.ErrorRecord]$ErrorRecord
    )

    # Start-Process may wrap the Win32 error in a plain InvalidOperationException, and Windows
    # localizes the message. Compare against the message for 1223 in the current UI language too.
    $declinedMessage = (New-Object System.ComponentModel.Win32Exception(1223)).Message

    $exception = $ErrorRecord.Exception
    while ($null -ne $exception) {
        $nativeErrorCodeProperty = $exception.PSObject.Properties['NativeErrorCode']
        if ($null -ne $nativeErrorCodeProperty -and $nativeErrorCodeProperty.Value -eq 1223) {
            return $true
        }

        if (($exception.HResult -band 0xFFFF) -eq 1223) {
            return $true
        }

        if ($exception.Message -match 'cancel(l)?ed by the user') {
            return $true
        }

        if (-not [string]::IsNullOrEmpty($declinedMessage) -and
            $exception.Message.IndexOf($declinedMessage, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }

        $exception = $exception.InnerException
    }

    return $false
}

if ($PSCmdlet.ParameterSetName -eq 'Install') {
    if (-not $isElevated) {
        throw "The install phase copies into %ProgramFiles% and must run elevated. Run the script without -PayloadDirectory from a normal-privilege PowerShell prompt; it downloads and verifies the release, then requests one UAC prompt for this copy."
    }

    $payloadFull = [System.IO.Path]::GetFullPath($PayloadDirectory)
    $resultPath = Join-Path $payloadFull $resultName
    $installSucceeded = $false
    $resultMessage = 'Install did not run.'
    $destinationPopulated = $false

    try {
        if (-not (Test-Path -LiteralPath $payloadFull -PathType Container)) {
            throw "Payload directory is missing or is not a directory: $payloadFull. The download phase creates it; run the script without -PayloadDirectory instead of calling the install phase by hand."
        }

        # Re-verify the payload: the elevated child trusts nothing it did not check itself.
        $manifest = Get-InstallManifest -Root $payloadFull -ManifestName $manifestName -SupportedVersion $supportedManifestVersion -Algorithm $manifestAlgorithm -RequiredNames $requiredExecutables
        $manifestFiles = $manifest.PSObject.Properties['files'].Value

        if (Test-Path -LiteralPath $destinationFull) {
            if (-not (Test-Path -LiteralPath $destinationFull -PathType Container)) {
                throw "Destination exists and is not a directory: $destinationFull"
            }

            $existing = @(Get-ChildItem -LiteralPath $destinationFull -Force)
            if ($existing.Count -gt 0) {
                throw "Destination is not empty: $destinationFull. Remove it or choose an empty directory."
            }
        }

        if (-not (Test-Path -LiteralPath $destinationFull)) {
            New-Item -ItemType Directory -Path $destinationFull -Force | Out-Null
        }

        # From here on, anything under $destinationFull was written by this phase. A failure removes it.
        $destinationPopulated = $true

        Copy-Item -Path (Join-Path $payloadFull '*') -Destination $destinationFull -Recurse -Force
        Get-ChildItem -LiteralPath $destinationFull -Recurse -File | Unblock-File

        Assert-ManifestFiles -ManifestFiles $manifestFiles -Root $destinationFull -RequiredNames $requiredExecutables -ManifestName $manifestName

        $cliPath = Join-Path $destinationFull 'SoulComputerUse.Cli.exe'
        & $cliPath --help | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "The installed CLI failed its self-check with exit code $LASTEXITCODE."
        }

        $installSucceeded = $true
        $resultMessage = "Installed to '$destinationFull'."
    }
    catch {
        $originalError = $_.Exception.Message
        if ($destinationPopulated) {
            try {
                Remove-Item -LiteralPath $destinationFull -Recurse -Force
                $resultMessage = "Install failed: $originalError Rollback removed the partial installation from '$destinationFull', so a retry is not blocked."
            }
            catch {
                $rollbackError = $_.Exception.Message
                $resultMessage = "Install failed: $originalError Rollback also failed: $rollbackError Remove '$destinationFull' manually before retrying."
            }
        }
        else {
            $resultMessage = "Install failed before the copy: $originalError"
        }
    }
    finally {
        $resultJson = [ordered]@{
            ok = $installSucceeded
            message = $resultMessage
        } | ConvertTo-Json -Compress

        try {
            if (Test-Path -LiteralPath $payloadFull -PathType Container) {
                [System.IO.File]::WriteAllText($resultPath, $resultJson, (New-Object System.Text.UTF8Encoding($false)))
            }
        }
        catch {
            [Console]::Error.WriteLine("Cannot write the install result to '$resultPath': $($_.Exception.Message)")
        }
    }

    if ($installSucceeded) {
        exit 0
    }

    [Console]::Error.WriteLine($resultMessage)
    exit 1
}

if ([string]::IsNullOrEmpty($PSCommandPath)) {
    throw "Cannot determine the script path for the elevated relaunch; run this script with -File so it can start its install phase."
}

if ($isElevated) {
    throw "The download phase downloads and verifies the release with normal user rights, then requests elevation only for the copy. Rerun from a non-elevated PowerShell prompt: & '$shellExecutable' -NoProfile -File '$PSCommandPath' -Repository '$Repository' -Version '$Version' -Destination '$destinationFull' If this account always runs elevated (UAC disabled), use the manual elevated steps in the skill's references/install.md."
}

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

    # Verify the extracted bytes before asking for elevation; the child repeats this check on its payload.
    $null = Get-InstallManifest -Root $extractRoot -ManifestName $manifestName -SupportedVersion $supportedManifestVersion -Algorithm $manifestAlgorithm -RequiredNames $requiredExecutables

    $childArguments = @(
        '-NoProfile'
        '-File'
        (ConvertTo-QuotedArgument -Value $PSCommandPath)
        '-PayloadDirectory'
        (ConvertTo-QuotedArgument -Value $extractRoot)
        '-Destination'
        (ConvertTo-QuotedArgument -Value $destinationFull)
    ) -join ' '

    try {
        $childProcess = Start-Process -FilePath $shellExecutable -ArgumentList $childArguments -Verb RunAs -Wait -PassThru -ErrorAction Stop
    }
    catch {
        if (Test-ElevationDeclined -ErrorRecord $_) {
            [Console]::Error.WriteLine("Elevation was declined, so nothing was copied and '$destinationFull' was not created. Run the script again and accept the UAC prompt to install.")
            exit 1
        }

        throw "Cannot start the elevated install phase with '$shellExecutable': $($_.Exception.Message) Run from an interactive desktop session so the UAC prompt can appear, or use the manual elevated steps in the skill's references/install.md."
    }

    $resultPath = Join-Path $extractRoot $resultName
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        [Console]::Error.WriteLine("The elevated install phase exited with code $($childProcess.ExitCode) without writing '$resultName'. The copy may be incomplete; inspect '$destinationFull' and remove a non-empty directory before retrying, because the installer refuses to overwrite one.")
        exit 1
    }

    try {
        $childResult = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        [Console]::Error.WriteLine("The elevated install phase wrote '$resultName' that is not valid JSON: $($_.Exception.Message). The installation result is unknown; inspect '$destinationFull' before retrying.")
        exit 1
    }

    if ($null -eq $childResult) {
        [Console]::Error.WriteLine("The elevated install phase wrote '$resultName' as JSON null. The installation result is unknown; inspect '$destinationFull' before retrying.")
        exit 1
    }

    $okProperty = $childResult.PSObject.Properties['ok']
    $messageProperty = $childResult.PSObject.Properties['message']
    if ($null -eq $okProperty -or $okProperty.Value -isnot [bool] -or $null -eq $messageProperty -or $messageProperty.Value -isnot [string]) {
        [Console]::Error.WriteLine("The elevated install phase wrote '$resultName' without a boolean 'ok' and a string 'message'. The installation result is unknown; inspect '$destinationFull' before retrying.")
        exit 1
    }

    if (-not $okProperty.Value) {
        [Console]::Error.WriteLine($messageProperty.Value)
        exit 1
    }

    if ($childProcess.ExitCode -ne 0) {
        [Console]::Error.WriteLine("The elevated install phase reported success but exited with code $($childProcess.ExitCode): $($messageProperty.Value)")
        exit 1
    }

    [pscustomobject]@{
        version = $release.tag_name
        installPath = $destinationFull
        manifest = Join-Path $destinationFull $manifestName
        host = Join-Path $destinationFull 'SoulComputerUse.Host.exe'
        cli = Join-Path $destinationFull 'SoulComputerUse.Cli.exe'
        mcp = Join-Path $destinationFull 'SoulComputerUse.Mcp.exe'
    } | ConvertTo-Json -Depth 3
    exit 0
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
