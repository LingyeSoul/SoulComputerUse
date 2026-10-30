using System;
using System.Collections.Generic;
using System.IO;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Guards the names that tie the packaging script, the installer, and the release workflow together.
/// They are three separate files with no compiler between them: a rename in one silently breaks the
/// download for every user, so the names are checked as data.
/// </summary>
[TestClass]
public sealed class ReleasePackagingTests
{
    private const string ApplicationArchiveName = "SoulComputerUse-win-x64.zip";
    private const string SkillArchiveName = "soul-computer-use-skill.zip";
    private const string ChecksumFileName = "SHA256SUMS.txt";

    [TestMethod]
    public void TheInstallerDownloadsTheArchiveThePackagingScriptBuilds()
    {
        string packaging = ReadRepositoryFile(Path.Combine("scripts", "package-release.ps1"));
        string installer = ReadRepositoryFile(Path.Combine("skills", "soul-computer-use", "scripts", "install.ps1"));

        Assert.Contains(ApplicationArchiveName, packaging, "The packaging script must produce the documented archive name.");
        Assert.Contains(ChecksumFileName, packaging, "The packaging script must produce the checksum file the installer verifies.");
        Assert.Contains(ApplicationArchiveName, installer, "The installer must download the archive the packaging script produces.");
        Assert.Contains(ChecksumFileName, installer, "The installer must verify against the checksum file the packaging script produces.");
    }

    [TestMethod]
    public void TheReleaseWorkflowUploadsEveryAssetTheInstallerAndSkillNeed()
    {
        string workflow = ReadRepositoryFile(Path.Combine(".github", "workflows", "release.yml"));

        Assert.Contains(ApplicationArchiveName, workflow);
        Assert.Contains(SkillArchiveName, workflow);
        Assert.Contains(ChecksumFileName, workflow);
        Assert.Contains("package-release.ps1", workflow, "The release must publish through the same script used locally.");
        Assert.Contains("--clobber", workflow, "Re-running a release must replace assets instead of failing on existing ones.");
    }

    [TestMethod]
    public void ThePackagingScriptRequiresEveryExecutableTheHostStartsFromItsOwnDirectory()
    {
        string packaging = ReadRepositoryFile(Path.Combine("scripts", "package-release.ps1"));

        // The host derives these two paths from its own directory; the CLI and MCP are the public entry
        // points. A release missing any of them installs and then fails at session start.
        string[] required =
        [
            "SoulComputerUse.Host.exe",
            "SoulComputerUse.SessionAgent.exe",
            "SoulComputerUse.ChildSessionsHelper.exe",
            "SoulComputerUse.Cli.exe",
            "SoulComputerUse.Mcp.exe",
        ];

        foreach (string executable in required)
        {
            Assert.Contains(executable, packaging, $"The packaging script must refuse a release without {executable}.");
        }
    }

    [TestMethod]
    public void ThePackagingScriptAndTheInstallerAgreeOnTheManifestVersion()
    {
        string packaging = ReadRepositoryFile(Path.Combine("scripts", "package-release.ps1"));
        string installer = ReadRepositoryFile(Path.Combine("skills", "soul-computer-use", "scripts", "install.ps1"));

        // Version 2 changed the key format from bare file names to '/'-separated relative paths, so a
        // script pair that disagrees would make every install refuse - or worse, best-effort read a
        // format it does not fully understand.
        Assert.Contains("manifestVersion = 2", packaging, "The packaging script must write the manifest version this build defines.");
        Assert.Contains("$supportedManifestVersion = 2", installer, "The installer must accept the manifest version the packaging script writes.");
    }

    /// <summary>Reads one repository file by walking up from the test output directory.</summary>
    private static string ReadRepositoryFile(string relativePath)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(
            $"'{relativePath}' was not found from the test output directory, so this check would pass by checking nothing.");
    }
}
