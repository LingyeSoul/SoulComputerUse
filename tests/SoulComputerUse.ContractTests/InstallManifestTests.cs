using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using SoulComputerUse.Windows.Integrity;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// The launch-time integrity check: a listed file whose SHA-256 hash matches the manifest is verified, and
/// every other state is a distinct status so a caller can refuse without parsing an exception.
/// </summary>
/// <remarks>
/// Every case builds its own layout under a temporary directory, so no case reads the test output directory
/// or an installed product.
/// </remarks>
[TestClass]
public sealed class InstallManifestTests
{
    private const string ExecutableName = "SoulComputerUse.Host.exe";
    private const string OtherExecutableName = "SoulComputerUse.Cli.exe";
    private const string SupportedAlgorithm = "SHA-256";
    private const int SupportedManifestVersion = 1;
    private const string WrongHash = "0000000000000000000000000000000000000000000000000000000000000000";

    [TestMethod]
    public void AListedFileWhoseHashMatchesIsVerified()
    {
        using TemporaryDirectory directory = new();
        string executablePath = WriteExecutable(directory.Path);
        TestInstallManifest.Write(
            directory.Path,
            SupportedManifestVersion,
            SupportedAlgorithm,
            ExecutableName,
            TestInstallManifest.Sha256Hex(executablePath));

        Assert.AreEqual(ExecutableIntegrity.Verified, Verify(executablePath, directory.Path));
    }

    [TestMethod]
    public void AnUppercaseManifestHashStillVerifies()
    {
        using TemporaryDirectory directory = new();
        string executablePath = WriteExecutable(directory.Path);
        TestInstallManifest.Write(
            directory.Path,
            SupportedManifestVersion,
            SupportedAlgorithm,
            ExecutableName,
            TestInstallManifest.Sha256Hex(executablePath).ToUpperInvariant());

        Assert.AreEqual(
            ExecutableIntegrity.Verified,
            Verify(executablePath, directory.Path),
            "The manifest hash comparison must be case-insensitive on the recorded hex.");
    }

    [TestMethod]
    public void AListedFileWhoseHashDiffersIsAMismatch()
    {
        using TemporaryDirectory directory = new();
        string executablePath = WriteExecutable(directory.Path);
        TestInstallManifest.Write(
            directory.Path,
            SupportedManifestVersion,
            SupportedAlgorithm,
            ExecutableName,
            WrongHash);

        Assert.AreEqual(ExecutableIntegrity.Mismatch, Verify(executablePath, directory.Path));
    }

    [TestMethod]
    public void AnExecutableTheManifestDoesNotListIsNotListed()
    {
        using TemporaryDirectory directory = new();
        TestInstallManifest.Write(
            directory.Path,
            SupportedManifestVersion,
            SupportedAlgorithm,
            OtherExecutableName,
            WrongHash);

        Assert.AreEqual(
            ExecutableIntegrity.NotListed,
            Verify(Path.Combine(directory.Path, ExecutableName), directory.Path),
            "An unlisted name must be refused before the file system is asked whether the file exists.");
    }

    [TestMethod]
    public void AnAbsentManifestIsNotInstalled()
    {
        using TemporaryDirectory directory = new();
        string executablePath = WriteExecutable(directory.Path);

        Assert.AreEqual(
            ExecutableIntegrity.NotInstalled,
            Verify(executablePath, directory.Path),
            "A source or test build has no manifest, which is not an error.");
    }

    [TestMethod]
    public void AListedExecutableThatIsAbsentIsFileMissing()
    {
        using TemporaryDirectory directory = new();
        TestInstallManifest.Write(
            directory.Path,
            SupportedManifestVersion,
            SupportedAlgorithm,
            ExecutableName,
            WrongHash);

        Assert.AreEqual(
            ExecutableIntegrity.FileMissing,
            Verify(Path.Combine(directory.Path, ExecutableName), directory.Path));
    }

    [TestMethod]
    public void AListedExecutableThatCannotBeReadIsUnreadable()
    {
        using TemporaryDirectory directory = new();
        string executablePath = WriteExecutable(directory.Path);
        TestInstallManifest.Write(
            directory.Path,
            SupportedManifestVersion,
            SupportedAlgorithm,
            ExecutableName,
            TestInstallManifest.Sha256Hex(executablePath));

        using FileStream locked = new(executablePath, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.AreEqual(
            ExecutableIntegrity.Unreadable,
            Verify(executablePath, directory.Path),
            "A listed file the process cannot open must be reported as unreadable, not as a mismatch.");
    }

    [TestMethod]
    public void InvalidJsonIsManifestUnreadable()
    {
        using TemporaryDirectory directory = new();
        string executablePath = WriteExecutable(directory.Path);
        TestInstallManifest.WriteRaw(directory.Path, "{ \"manifestVersion\": ");

        Assert.AreEqual(ExecutableIntegrity.ManifestUnreadable, Verify(executablePath, directory.Path));
    }

    [TestMethod]
    public void AnUnsupportedManifestVersionIsManifestUnreadable()
    {
        using TemporaryDirectory directory = new();
        string executablePath = WriteExecutable(directory.Path);
        TestInstallManifest.Write(
            directory.Path,
            2,
            SupportedAlgorithm,
            ExecutableName,
            WrongHash);

        Assert.AreEqual(ExecutableIntegrity.ManifestUnreadable, Verify(executablePath, directory.Path));
    }

    [TestMethod]
    public void AnUnsupportedAlgorithmIsManifestUnreadable()
    {
        using TemporaryDirectory directory = new();
        string executablePath = WriteExecutable(directory.Path);
        TestInstallManifest.Write(
            directory.Path,
            SupportedManifestVersion,
            "SHA-512",
            ExecutableName,
            WrongHash);

        Assert.AreEqual(ExecutableIntegrity.ManifestUnreadable, Verify(executablePath, directory.Path));
    }

    [TestMethod]
    public void AFileHashThatIsNotAStringIsManifestUnreadable()
    {
        using TemporaryDirectory directory = new();
        string executablePath = WriteExecutable(directory.Path);
        TestInstallManifest.WriteRaw(
            directory.Path,
            "{\"manifestVersion\":1,\"productVersion\":\"1.2.3\",\"algorithm\":\"SHA-256\",\"files\":{\""
            + ExecutableName + "\":42}}");

        Assert.AreEqual(
            ExecutableIntegrity.ManifestUnreadable,
            Verify(executablePath, directory.Path),
            "A files object of strings must reject a hash that is not a string.");
    }

    [TestMethod]
    public void AnEmptyExecutablePathIsRejected()
    {
        using TemporaryDirectory directory = new();

        Assert.ThrowsExactly<ArgumentException>(
            () => InstallManifest.Verify("   ", Path.Combine(directory.Path, InstallManifest.FileName)));
    }

    private static ExecutableIntegrity Verify(string executablePath, string directory)
    {
        return InstallManifest.Verify(executablePath, Path.Combine(directory, InstallManifest.FileName));
    }

    private static string WriteExecutable(string directory)
    {
        string executablePath = Path.Combine(directory, ExecutableName);
        File.WriteAllBytes(executablePath, [0x4D, 0x5A]);
        return executablePath;
    }
}

/// <summary>Writes installation manifests for the contract tests that need a real installed layout.</summary>
internal static class TestInstallManifest
{
    private const string SupportedProductVersion = "1.2.3";

    /// <summary>Returns the lowercase SHA-256 hash of one file, as the manifest records it.</summary>
    internal static string Sha256Hex(string filePath)
    {
        return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(filePath)));
    }

    /// <summary>Writes a valid manifest that lists exactly one file.</summary>
    internal static void Write(
        string directory,
        int manifestVersion,
        string algorithm,
        string fileName,
        string hash)
    {
        WriteRaw(
            directory,
            "{\"manifestVersion\":" + manifestVersion.ToString(CultureInfo.InvariantCulture)
            + ",\"productVersion\":\"" + SupportedProductVersion + "\",\"algorithm\":\"" + algorithm
            + "\",\"files\":{\"" + fileName + "\":\"" + hash + "\"}}");
    }

    /// <summary>Writes the given text as the manifest, which tests use for invalid documents.</summary>
    internal static void WriteRaw(string directory, string json)
    {
        File.WriteAllText(Path.Combine(directory, InstallManifest.FileName), json);
    }
}
