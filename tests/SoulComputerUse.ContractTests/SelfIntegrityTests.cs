using SoulComputerUse.Windows.Integrity;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// The startup self-check every entry point runs before it does any work: a source or test build without a
/// manifest continues, and every status that disagrees with the installed manifest is a refusal.
/// </summary>
/// <remarks>
/// The entry points are process-level and cannot be started from this test project, so these cases cover the
/// decision the gate makes rather than the four command lines that call it.
/// </remarks>
[TestClass]
public sealed class SelfIntegrityTests
{
    [TestMethod]
    public void ATestHostWithoutAManifestIsNotInstalledAndAccepted()
    {
        // This is the source and test-build path: no installation manifest sits beside the test host, so the
        // self-check must accept the process rather than refuse every test run.
        ExecutableIntegrity status = SelfIntegrity.Check();

        Assert.AreEqual(ExecutableIntegrity.NotInstalled, status);
        Assert.IsTrue(SelfIntegrity.IsAccepted(status));
    }

    [TestMethod]
    public void OnlyVerifiedAndNotInstalledAreAccepted()
    {
        Assert.IsTrue(SelfIntegrity.IsAccepted(ExecutableIntegrity.Verified));
        Assert.IsTrue(SelfIntegrity.IsAccepted(ExecutableIntegrity.NotInstalled));

        ExecutableIntegrity[] refused =
        [
            ExecutableIntegrity.ManifestUnreadable,
            ExecutableIntegrity.NotListed,
            ExecutableIntegrity.FileMissing,
            ExecutableIntegrity.Mismatch,
            ExecutableIntegrity.Unreadable,
        ];

        foreach (ExecutableIntegrity status in refused)
        {
            Assert.IsFalse(SelfIntegrity.IsAccepted(status), $"{status} must be refused.");
        }
    }
}
