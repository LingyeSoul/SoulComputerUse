using System.IO;
using SoulComputerUse.Windows.ChildSessions;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// The elevator refuses a helper that does not match the installation manifest before the STA thread that
/// would show the consent prompt exists, so a replaced helper never gains administrator authority.
/// </summary>
/// <remarks>
/// The case uses a two-byte stub as the helper and a wrong hash, so the refusal is decided before any
/// process launch and nothing on the machine changes.
/// </remarks>
[TestClass]
public sealed class ChildSessionsElevatorIntegrityTests
{
    [TestMethod]
    public void AHelperThatDoesNotMatchTheManifestIsRefusedWithoutElevation()
    {
        using TemporaryDirectory directory = new();
        File.WriteAllBytes(
            Path.Combine(directory.Path, ChildSessionsElevator.HelperFileName),
            [0x4D, 0x5A]);
        TestInstallManifest.Write(
            directory.Path,
            1,
            "SHA-256",
            ChildSessionsElevator.HelperFileName,
            new string('0', 64));

        ElevationResult result = ChildSessionsElevator.EnableChildSessions(directory.Path, 1000);

        Assert.IsTrue(result.Failed, "An integrity refusal must not report child sessions as enabled.");
        Assert.AreEqual(ElevationFailure.IntegrityRefused, result.Failure);
        Assert.AreEqual(
            0,
            result.ErrorCode,
            "No helper process ran, so no helper exit code can be reported.");
    }
}
