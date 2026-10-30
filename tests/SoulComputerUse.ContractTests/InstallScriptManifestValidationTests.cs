using System;
using System.Diagnostics;
using System.IO;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// The installer's manifest validation on the PowerShell side: the payload check must fail closed in
/// both directions - a listed file that is missing, and a real file the manifest does not list.
/// </summary>
/// <remarks>
/// <para>
/// <c>Assert-ManifestFiles</c> lives in <c>skills/soul-computer-use/scripts/install.ps1</c>, which has
/// no compiler, so this case extracts the real function bodies from the shipped script through the
/// PowerShell parser and runs them in one child shell. The scenarios never elevate, never touch
/// Program Files, and never reach the network: they call the function directly on directories under
/// the test's temporary root. The paths cross the process boundary through environment variables,
/// because quoting paths into a PowerShell command line is exactly the kind of parsing this suite
/// should not depend on.
/// </para>
/// <para>
/// A machine with no PowerShell at all fails the case instead of skipping it, because a skipped
/// validation case is a green check that checked nothing.
/// </para>
/// </remarks>
[TestClass]
public sealed class InstallScriptManifestValidationTests
{
    private const string InstallScriptVariable = "SCU_TEST_INSTALL_SCRIPT";
    private const string WorkDirectoryVariable = "SCU_TEST_WORK_DIRECTORY";

    /// <summary>Runs every scenario in one child shell and requires the exact outcome for each.</summary>
    [TestMethod]
    public void TheInstallersPayloadCheckFailsClosedInBothDirections()
    {
        string installScript = FindRepositoryFile(Path.Combine("skills", "soul-computer-use", "scripts", "install.ps1"));

        using TemporaryDirectory work = new();
        string driverPath = Path.Combine(work.Path, "manifest-validation-driver.ps1");
        File.WriteAllText(driverPath, BuildDriverScript());

        string shell = FindPowerShell();
        string output = RunScript(shell, driverPath, installScript, work.Path);

        Assert.Contains("SCENARIO exact OK", output, "A payload whose files exactly match the manifest must be accepted. Output: " + output);
        AssertScenarioRefused(output, "unlisted-file", "does not list");
        AssertScenarioRefused(output, "listed-file-missing", "missing from");
        AssertScenarioRefused(output, "parent-hop-key", "safe '/'-separated");
        AssertScenarioRefused(output, "absolute-path-key", "safe '/'-separated");
    }

    /// <summary>Asserts that one scenario line reports a refusal carrying the expected reason.</summary>
    private static void AssertScenarioRefused(string output, string scenario, string expectedReason)
    {
        string marker = "SCENARIO " + scenario + " REFUSED: ";
        int index = output.IndexOf(marker, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, index, $"The '{scenario}' scenario must be refused. Output: {output}");
        Assert.IsGreaterThanOrEqualTo(
            0,
            output.IndexOf(expectedReason, index, StringComparison.Ordinal),
            $"The '{scenario}' refusal must say '{expectedReason}'. Output: {output}");
    }

    /// <summary>Builds the driver that defines the shipped functions and exercises them per scenario.</summary>
    private static string BuildDriverScript()
    {
        return """
            $ErrorActionPreference = 'Stop'

            $installScriptPath = $env:SCU_TEST_INSTALL_SCRIPT
            $workDirectory = $env:SCU_TEST_WORK_DIRECTORY
            if ([string]::IsNullOrEmpty($installScriptPath) -or [string]::IsNullOrEmpty($workDirectory)) {
                throw 'The driver did not receive its input paths through the environment.'
            }

            # Define the shipped functions by parsing the real installer, so these scenarios run the
            # bytes a user downloads, not a copy that could drift.
            $parseErrors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($installScriptPath, [ref]$null, [ref]$parseErrors)
            if ($parseErrors.Count -gt 0) {
                throw "install.ps1 does not parse: $($parseErrors[0].Message)"
            }

            $needed = @('Test-ManifestKey', 'Get-ManifestKeyForFile', 'Assert-ManifestFiles')
            $functions = @($ast.FindAll({ param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $needed -contains $node.Name
            }, $true))
            if ($functions.Count -ne $needed.Count) {
                throw "Could not extract the manifest validation functions from install.ps1."
            }

            foreach ($function in $functions) {
                Invoke-Expression $function.Extent.Text
            }

            function New-Payload {
                param([string]$Name)
                $root = Join-Path $workDirectory $Name
                New-Item -ItemType Directory -Path (Join-Path $root 'sub') -Force | Out-Null
                return $root
            }

            function New-Manifest {
                param([string]$Root, [System.Collections.IDictionary]$Entries)
                $entriesJson = ($Entries.GetEnumerator() | ForEach-Object {
                    '  "{0}": "{1}"' -f $_.Key, $_.Value
                }) -join ",`n"
                $json = "{`n `"manifestVersion`": 2,`n `"algorithm`": `"SHA-256`",`n `"files`": {`n$entriesJson`n }`n}"
                [System.IO.File]::WriteAllText((Join-Path $Root 'install-manifest.json'), $json)
                return [pscustomobject]$Entries
            }

            function Get-Hash {
                param([string]$Path)
                return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
            }

            function Invoke-Scenario {
                param([string]$Name, [scriptblock]$Body)
                try {
                    & $Body
                    Write-Output "SCENARIO $Name OK"
                }
                catch {
                    $reason = ($_.Exception.Message -replace "`r?`n", ' ')
                    Write-Output "SCENARIO $Name REFUSED: $reason"
                }
            }

            $manifestName = 'install-manifest.json'
            $required = @('a.exe')
            $zeroHash = New-Object string ('0', 64)

            Invoke-Scenario 'exact' {
                # The manifest lists every file, including one in a subdirectory, with matching hashes.
                $root = New-Payload 'exact'
                Set-Content -LiteralPath (Join-Path $root 'a.exe') -Value 'main executable' -NoNewline
                Set-Content -LiteralPath (Join-Path $root 'sub\b.dll') -Value 'nested library' -NoNewline
                Assert-ManifestFiles -ManifestFiles (New-Manifest $root ([ordered]@{
                    'a.exe' = Get-Hash (Join-Path $root 'a.exe')
                    'sub/b.dll' = Get-Hash (Join-Path $root 'sub\b.dll')
                })) -Root $root -RequiredNames $required -ManifestName $manifestName | Out-Null
            }

            Invoke-Scenario 'unlisted-file' {
                # The payload carries evil.dll but the manifest does not list it: the reverse check must refuse.
                $root = New-Payload 'unlisted-file'
                Set-Content -LiteralPath (Join-Path $root 'a.exe') -Value 'main executable' -NoNewline
                Set-Content -LiteralPath (Join-Path $root 'evil.dll') -Value 'unlisted payload' -NoNewline
                Assert-ManifestFiles -ManifestFiles (New-Manifest $root ([ordered]@{
                    'a.exe' = Get-Hash (Join-Path $root 'a.exe')
                })) -Root $root -RequiredNames $required -ManifestName $manifestName | Out-Null
                throw 'The unlisted payload file was accepted.'
            }

            Invoke-Scenario 'listed-file-missing' {
                # The manifest lists ghost.dll, which does not exist on disk.
                $root = New-Payload 'listed-file-missing'
                Set-Content -LiteralPath (Join-Path $root 'a.exe') -Value 'main executable' -NoNewline
                Assert-ManifestFiles -ManifestFiles (New-Manifest $root ([ordered]@{
                    'a.exe' = Get-Hash (Join-Path $root 'a.exe')
                    'ghost.dll' = $zeroHash
                })) -Root $root -RequiredNames $required -ManifestName $manifestName | Out-Null
                throw 'The missing listed file was accepted.'
            }

            Invoke-Scenario 'parent-hop-key' {
                # A key that climbs out of the payload root.
                $root = New-Payload 'parent-hop-key'
                Set-Content -LiteralPath (Join-Path $root 'a.exe') -Value 'main executable' -NoNewline
                Assert-ManifestFiles -ManifestFiles (New-Manifest $root ([ordered]@{
                    'a.exe' = Get-Hash (Join-Path $root 'a.exe')
                    '../evil.exe' = $zeroHash
                })) -Root $root -RequiredNames $required -ManifestName $manifestName | Out-Null
                throw 'The parent-hop key was accepted.'
            }

            Invoke-Scenario 'absolute-path-key' {
                # A key anchored at a drive.
                $root = New-Payload 'absolute-path-key'
                Set-Content -LiteralPath (Join-Path $root 'a.exe') -Value 'main executable' -NoNewline
                Assert-ManifestFiles -ManifestFiles (New-Manifest $root ([ordered]@{
                    'a.exe' = Get-Hash (Join-Path $root 'a.exe')
                    'C:/Windows/evil.dll' = $zeroHash
                })) -Root $root -RequiredNames $required -ManifestName $manifestName | Out-Null
                throw 'The absolute-path key was accepted.'
            }
            """;
    }

    /// <summary>Runs the driver and fails with both streams in the message when the shell itself fails.</summary>
    private static string RunScript(string shell, string driverPath, string installScriptPath, string workDirectory)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = shell,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        process.StartInfo.EnvironmentVariables[InstallScriptVariable] = installScriptPath;
        process.StartInfo.EnvironmentVariables[WorkDirectoryVariable] = workDirectory;
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(driverPath);

        process.Start();
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(60_000))
        {
            process.Kill();
            throw new AssertFailedException("The manifest validation driver did not finish within 60 seconds.");
        }

        if (process.ExitCode != 0)
        {
            throw new AssertFailedException(
                $"The manifest validation driver failed with exit code {process.ExitCode}. Output: {output}");
        }

        return output;
    }

    /// <summary>Finds pwsh when installed and otherwise Windows PowerShell, which the script also supports.</summary>
    private static string FindPowerShell()
    {
        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVariable))
        {
            foreach (string directory in pathVariable.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                foreach (string executable in new[] { "pwsh.exe", "powershell.exe" })
                {
                    try
                    {
                        string candidate = Path.Combine(directory.Trim().Trim('"'), executable);
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // A malformed PATH entry cannot hold the shell; keep scanning the usable ones.
                    }
                }
            }
        }

        throw new AssertFailedException(
            "Neither pwsh.exe nor powershell.exe was found on PATH, so the installer's manifest validation cannot be exercised.");
    }

    /// <summary>Reads one repository file by walking up from the test output directory.</summary>
    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new AssertFailedException(
            $"'{relativePath}' was not found from the test output directory, so this check would pass by checking nothing.");
    }
}
