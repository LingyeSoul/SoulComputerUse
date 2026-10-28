using System;
using System.IO;
using SoulComputerUse.Windows.Hosting.Launch;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// The host executable must be derived from this program's own directory, so a launch can never be
/// pointed at a program that merely shares the file name.
/// </summary>
[TestClass]
public sealed class HostLaunchGuardTests
{
    [TestMethod]
    public void TheHostExecutableIsDerivedFromTheApplicationDirectory()
    {
        using TemporaryDirectory directory = new();
        File.WriteAllBytes(Path.Combine(directory.Path, HostExecutablePath.FileName), [0x4D, 0x5A]);
        string resolved = HostExecutablePath.Resolve(directory.Path);
        Assert.AreEqual(Path.GetFullPath(Path.Combine(directory.Path, HostExecutablePath.FileName)), resolved);
    }

    [TestMethod]
    public void AMissingHostExecutableNamesOnlyTheFileName()
    {
        using TemporaryDirectory directory = new();
        FileNotFoundException error = Assert.ThrowsExactly<FileNotFoundException>(
            () => HostExecutablePath.Resolve(directory.Path));
        Assert.AreEqual(HostExecutablePath.FileName, error.FileName);
        Assert.DoesNotContain(directory.Path, error.Message);
    }

    [TestMethod]
    public void AnEmptyApplicationDirectoryIsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(() => HostExecutablePath.Resolve(string.Empty));
        Assert.ThrowsExactly<ArgumentException>(() => HostExecutablePath.Resolve("   "));
    }

    [TestMethod]
    public void AProcessOutsideTheConsoleSessionIsRefused()
    {
        ConsoleSessionGuard guard = new(
            readProcessSessionId: static () => 2,
            readConsoleSessionId: static () => 1);

        Assert.IsFalse(
            guard.IsCurrentProcessOnConsoleSession(),
            "A process in a remote desktop or disconnected session must never start a host window.");
    }

    [TestMethod]
    public void AProcessOnTheConsoleSessionIsAccepted()
    {
        ConsoleSessionGuard guard = new(
            readProcessSessionId: static () => 1,
            readConsoleSessionId: static () => 1);

        Assert.IsTrue(guard.IsCurrentProcessOnConsoleSession());
    }

    [TestMethod]
    public void AnUnreadableConsoleSessionFailsClosed()
    {
        ConsoleSessionGuard guard = new(
            readProcessSessionId: static () => 1,
            readConsoleSessionId: static () => throw new InvalidOperationException("No session is attached to the console."));

        Assert.IsFalse(
            guard.IsCurrentProcessOnConsoleSession(),
            "An unreadable console session is not one a launch may rely on, so the guard must refuse instead of throwing.");
    }

    [TestMethod]
    public void AnUnreadableProcessSessionFailsClosed()
    {
        ConsoleSessionGuard guard = new(
            readProcessSessionId: static () => throw new InvalidOperationException("ProcessIdToSessionId failed for this process."),
            readConsoleSessionId: static () => 1);

        Assert.IsFalse(
            guard.IsCurrentProcessOnConsoleSession(),
            "An unreadable process session is not a proven console session, so the guard must refuse instead of throwing.");
    }
}
