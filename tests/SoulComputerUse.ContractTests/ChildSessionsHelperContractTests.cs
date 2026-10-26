using System;
using System.IO;
using SoulComputerUse.Windows.ChildSessions;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// The elevation helper's command line contract: exactly one verb, nothing else. This is the whole
/// surface that runs elevated, so the accepted shapes are pinned here rather than discovered at
/// runtime.
/// </summary>
[TestClass]
public sealed class ChildSessionsHelperContractTests
{
    [TestMethod]
    public void ProbeVerbIsAccepted()
    {
        Assert.AreEqual(
            ChildSessionsHelperVerb.Probe,
            ChildSessionsHelperContract.ParseArguments(["--verb", "probe"]));
    }

    [TestMethod]
    public void EnableVerbIsAccepted()
    {
        Assert.AreEqual(
            ChildSessionsHelperVerb.Enable,
            ChildSessionsHelperContract.ParseArguments(["--verb", "enable"]));
    }

    [TestMethod]
    public void NoArgumentsAreRefused()
    {
        Assert.IsNull(ChildSessionsHelperContract.ParseArguments([]));
    }

    [TestMethod]
    public void ExtraArgumentsAreRefused()
    {
        Assert.IsNull(ChildSessionsHelperContract.ParseArguments(["--verb", "enable", "extra"]));
        Assert.IsNull(ChildSessionsHelperContract.ParseArguments(["--verb", "enable", "--force"]));
    }

    [TestMethod]
    public void UnknownVerbIsRefused()
    {
        Assert.IsNull(ChildSessionsHelperContract.ParseArguments(["--verb", "disable"]));
        Assert.IsNull(ChildSessionsHelperContract.ParseArguments(["--verb", "DeleteEverything"]));
    }

    [TestMethod]
    public void UnknownSwitchIsRefused()
    {
        Assert.IsNull(ChildSessionsHelperContract.ParseArguments(["--path", "enable"]));
        Assert.IsNull(ChildSessionsHelperContract.ParseArguments(["enable"]));
    }

    [TestMethod]
    public void VerbWordsMatchTheAcceptedVocabulary()
    {
        Assert.AreEqual("probe", ChildSessionsHelperContract.ToArgument(ChildSessionsHelperVerb.Probe));
        Assert.AreEqual("enable", ChildSessionsHelperContract.ToArgument(ChildSessionsHelperVerb.Enable));
    }

    [TestMethod]
    public void ExitCodesLetACallerTellSuccessFromRefusalFromBadUsage()
    {
        int[] exitCodes =
        [
            ChildSessionsHelperContract.SuccessExitCode,
            ChildSessionsHelperContract.SystemRefusedExitCode,
            ChildSessionsHelperContract.BadArgumentsExitCode,
        ];

        CollectionAssert.AllItemsAreUnique(
            exitCodes,
            "The caller decides from the exit code alone, so success, refusal and bad usage must never share a code.");
        Assert.AreEqual(0, exitCodes[0], "Zero means success, as every launcher expects.");
        Assert.IsTrue(
            exitCodes[1] != 0 && exitCodes[2] != 0,
            "Both failure codes must be non-zero so a launcher never reads a failure as success.");
    }
}

/// <summary>
/// The helper path must be derived from this program's own directory and must never come from an
/// argument, because an elevated launch accepts no path from outside.
/// </summary>
[TestClass]
public sealed class ChildSessionsElevatorPathTests
{
    [TestMethod]
    public void HelperPathIsDerivedFromTheApplicationDirectory()
    {
        string applicationDirectory = Path.Combine(Path.GetTempPath(), "scu-elevator-" + Guid.NewGuid().ToString("N"));

        FileNotFoundException error = Assert.ThrowsExactly<FileNotFoundException>(
            () => ChildSessionsElevator.ResolveHelperPath(applicationDirectory));

        StringAssert.Contains(
            error.Message,
            ChildSessionsElevator.HelperFileName,
            "The error must name the helper it looked for, so the operator can see what is missing.");
        Assert.IsFalse(
            error.Message.Contains(applicationDirectory, StringComparison.OrdinalIgnoreCase),
            "The message must never carry the real installation path, because it can reach a console, a screenshot or an issue.");
    }

    [TestMethod]
    public void ExistingHelperIsResolvedToAnAbsolutePathInsideTheApplicationDirectory()
    {
        string applicationDirectory = Path.Combine(Path.GetTempPath(), "scu-elevator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(applicationDirectory);
        try
        {
            string helperPath = Path.Combine(applicationDirectory, ChildSessionsElevator.HelperFileName);
            File.WriteAllText(helperPath, string.Empty);

            string resolved = ChildSessionsElevator.ResolveHelperPath(applicationDirectory);

            Assert.AreEqual(Path.GetFullPath(helperPath), resolved, "The helper must resolve beside the program.");
            Assert.IsTrue(Path.IsPathFullyQualified(resolved), "An elevated launch needs a full path, not a name resolved through PATH.");
        }
        finally
        {
            Directory.Delete(applicationDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void EmptyApplicationDirectoryIsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ChildSessionsElevator.ResolveHelperPath(string.Empty));
    }
}