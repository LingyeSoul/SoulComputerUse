using System;
using Microsoft.Win32;

namespace SoulComputerUse.Windows.ChildSessions;

/// <summary>
/// Mounts the child-session agent as this account's temporary <c>Run</c> start-up entry, so the child
/// session's desktop shell starts the agent the moment Windows has logged the session on.
/// </summary>
/// <remarks>
/// <para>
/// A child session ignores the RDP <c>StartProgram</c> property: it is a loopback session that Windows
/// logs on by itself and always starts the standard desktop shell, so the alternate-shell property the
/// connection writes is accepted and then never applied — measured on the development machine on
/// 2026-10-27, the child session's process list held a full desktop (explorer, sihost, taskhostw) and
/// the agent had not started. The <c>Run</c> entry is what reaches inside instead: the child session is
/// this same account, so its shell reads the same <c>HKCU</c> hive and launches the entry on logon.
/// </para>
/// <para>
/// The entry is this run's and this run's only: it is installed before the connection and removed on
/// every exit path, the same symmetric pattern the machine-wide child-session switch uses. A crash that
/// skips the removal leaves the entry behind, and the next console logon would start the agent there —
/// which the agent's own console gate refuses at once, so the leftover costs one log line and nothing
/// else.
/// </para>
/// <para>
/// The command line carries the binding nonce, exactly as the alternate-shell property did: the value
/// is readable by processes of this account for as long as the entry exists, which the pipe's DACL and
/// peer-SID check already treat as the trust boundary.
/// </para>
/// </remarks>
public static class ChildSessionAgentBootstrap
{
    /// <summary>The <c>Run</c> value name this product owns.</summary>
    public const string RunValueName = "SoulComputerUse.ChildSessionAgent";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Writes the start-up entry and reads it back, because an entry that did not stick would leave the
    /// child session without an agent exactly as a <c>StartProgram</c> write that did not stick does.
    /// </summary>
    /// <exception cref="ArgumentException">The command line is empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// The <c>Run</c> key could not be opened, or the written value did not survive the read-back.
    /// </exception>
    public static void Install(string commandLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

        using RegistryKey runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath)
            ?? throw new InvalidOperationException(
                $"The current user's Run key '{RunKeyPath}' could not be created, so the child-session agent cannot be started on logon.");

        runKey.SetValue(RunValueName, commandLine);
        string? readBack = Read();
        if (!string.Equals(readBack, commandLine, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The child-session agent start-up entry did not survive being written, so the child session would start without an agent.");
        }
    }

    /// <summary>
    /// Removes the start-up entry. An entry that is already gone is success: both exit paths call this,
    /// and a second call after a successful session close must not fail the run.
    /// </summary>
    public static void Remove()
    {
        using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (runKey is null)
        {
            return;
        }

        if (runKey.GetValue(RunValueName) is not null)
        {
            runKey.DeleteValue(RunValueName);
        }
    }

    /// <summary>Reads the mounted command line, or null when no entry exists.</summary>
    public static string? Read()
    {
        using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return runKey?.GetValue(RunValueName) as string;
    }
}
