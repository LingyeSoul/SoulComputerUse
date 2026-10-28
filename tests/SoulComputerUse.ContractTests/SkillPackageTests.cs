using System;
using System.Collections.Generic;
using System.IO;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Guards the agent-skill package that the release ships. The Vercel skills CLI discovers a skill by
/// its <c>SKILL.md</c> frontmatter and installs the whole directory, so a missing file or a renamed
/// skill breaks installation without breaking the product build.
/// </summary>
[TestClass]
public sealed class SkillPackageTests
{
    private const string SkillName = "soul-computer-use";

    [TestMethod]
    public void TheSkillFrontmatterNamesTheSkillAndDescribesItsTrigger()
    {
        string[] lines = File.ReadAllLines(SkillFile());

        Assert.IsGreaterThanOrEqualTo(4, lines.Length, "SKILL.md must open with a YAML frontmatter block.");
        Assert.AreEqual("---", lines[0], "The frontmatter must start on the first line.");
        Assert.AreEqual("---", lines[3], "The frontmatter must hold exactly name and description.");

        Assert.AreEqual($"name: {SkillName}", lines[1], "The skill name must match its directory, or the CLI cannot address it.");
        Assert.StartsWith("description: ", lines[2], "A skill without a description is never selected.");
        Assert.IsGreaterThan(40, lines[2].Length, "The description must say when the skill applies, not just what it is.");
    }

    [TestMethod]
    public void EveryFileTheSkillPointsAtShipsWithIt()
    {
        string skillDirectory = Path.GetDirectoryName(SkillFile())
            ?? throw new InvalidOperationException("The skill file has no directory.");

        string[] required =
        [
            Path.Combine("references", "install.md"),
            Path.Combine("references", "build-from-source.md"),
            Path.Combine("references", "usage.md"),
            Path.Combine("scripts", "install.ps1"),
        ];

        foreach (string relative in required)
        {
            string path = Path.Combine(skillDirectory, relative);
            Assert.IsTrue(File.Exists(path), $"The skill references '{relative}' but it is missing from the package.");
        }
    }

    [TestMethod]
    public void TheSkillNeverInvitesACredentialIntoTheProduct()
    {
        string text = File.ReadAllText(SkillFile());

        Assert.Contains("Never ask for, type, inject, or store a password", text);
        Assert.Contains("awaiting-credentials", text);
    }

    [TestMethod]
    public void TheSkillTellsTheAgentToStartTheSessionBeforePolling()
    {
        string text = File.ReadAllText(SkillFile());

        Assert.Contains("scu start", text);
        Assert.Contains("start_session", text);
        Assert.Contains("E_HOST_LAUNCH_FAILED", text);
        Assert.DoesNotContain("Start the GUI host, then let the human complete the Windows login", text);
    }

    /// <summary>Locates the shipped skill by walking up from the test output directory.</summary>
    private static string SkillFile()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, "skills", SkillName, "SKILL.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(
            "The shipped skill was not found from the test output directory, so the package check would pass by checking nothing.");
    }
}
