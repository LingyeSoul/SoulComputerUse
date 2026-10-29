using System;

namespace SoulComputerUse.Windows.Integrity;

/// <summary>
/// Verifies this process's own executable against the installation manifest before the program does any work.
/// </summary>
/// <remarks>
/// <para>
/// The launch-time checks in <see cref="InstallManifest"/> only cover the product's own launch paths: the host
/// launcher verifies the host before it starts it, and the elevation helper verifies itself. A host started
/// directly by a double-click or by <c>Start-Process</c> bypasses the launcher, and a CLI, MCP or agent process
/// started directly never passes a launching verifier at all, so each entry point must verify its own file and
/// refuse to run when it does not match the installed manifest.
/// </para>
/// <para>
/// A process that cannot name its own file has no installed manifest to compare against, so it is treated as
/// <see cref="ExecutableIntegrity.NotInstalled"/>. That keeps source and test hosts usable, while the installed
/// layout still verifies because its manifest sits beside the binaries in an administrator-protected directory.
/// </para>
/// </remarks>
public static class SelfIntegrity
{
    /// <summary>The refusal every entry point reports when its own file does not match the installation manifest.</summary>
    public const string FailureMessage =
        "The installed files do not match the installation manifest, so this program was not started. Reinstall the "
        + "product to restore the files the installation delivered.";

    /// <summary>Verifies this process's executable against the installation manifest beside it.</summary>
    /// <returns>
    /// The comparison status, or <see cref="ExecutableIntegrity.NotInstalled"/> when the process cannot name its
    /// own file.
    /// </returns>
    public static ExecutableIntegrity Check()
    {
        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            return ExecutableIntegrity.NotInstalled;
        }

        return InstallManifest.Verify(processPath);
    }

    /// <summary>Returns whether a status allows the program to continue.</summary>
    /// <param name="status">The status to judge.</param>
    /// <returns>
    /// <see langword="true"/> only for <see cref="ExecutableIntegrity.Verified"/> and
    /// <see cref="ExecutableIntegrity.NotInstalled"/>.
    /// </returns>
    public static bool IsAccepted(ExecutableIntegrity status)
    {
        return status is ExecutableIntegrity.Verified or ExecutableIntegrity.NotInstalled;
    }
}
