using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SoulComputerUse.Windows.Native;

namespace SoulComputerUse.Windows.ChildSessions;

/// <summary>The two machine policies that must allow a child session before anything is connected.</summary>
public sealed record ChildSessionHardPreconditions(PolicyState RdpInbound, PolicyState ChildConnections);

/// <summary>
/// Reads and changes the machine-wide child-session capability. Enabling and disabling both need
/// administrator rights, and the change takes effect immediately: a clean machine has it off.
/// </summary>
public static class ChildSessionsProbe
{
    private const string TerminalServerKeyPath = @"SYSTEM\CurrentControlSet\Control\Terminal Server";

    /// <summary>Reports whether the machine currently allows child sessions. Never guesses.</summary>
    public static bool IsEnabled()
    {
        if (!NativeMethods.WTSIsChildSessionsEnabled(out bool enabled))
        {
            int errorCode = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                errorCode,
                $"WTSIsChildSessionsEnabled failed with Win32 error {errorCode}, so the child-session state cannot be read.");
        }

        return enabled;
    }

    /// <summary>
    /// Turns the machine-wide child-session capability on or off. Requires administrator rights;
    /// a refusal is surfaced as a <see cref="Win32Exception"/> instead of being retried or hidden.
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        if (!NativeMethods.WTSEnableChildSessions(enabled))
        {
            int errorCode = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                errorCode,
                $"WTSEnableChildSessions(enabled: {enabled}) failed with Win32 error {errorCode}.");
        }
    }

    /// <summary>Reads both hard preconditions from the machine policy.</summary>
    public static ChildSessionHardPreconditions ReadHardPreconditions()
    {
        using RegistryKey terminalServerKey = Registry.LocalMachine.OpenSubKey(TerminalServerKeyPath)
            ?? throw new InvalidOperationException(
                $"The Terminal Server policy key '{TerminalServerKeyPath}' could not be opened, so the hard preconditions cannot be read.");

        return new ChildSessionHardPreconditions(
            InterpretPolicyValue(ChildSessionsPolicy.RdpInboundValueName, terminalServerKey.GetValue(ChildSessionsPolicy.RdpInboundValueName)),
            InterpretPolicyValue(ChildSessionsPolicy.ChildConnectionsValueName, terminalServerKey.GetValue(ChildSessionsPolicy.ChildConnectionsValueName)));
    }

    /// <summary>
    /// Turns one raw policy value into a state. A missing value is <see cref="PolicyState.Missing"/>,
    /// which callers must treat as a refusal: an absent policy value is not consent, and connecting
    /// anyway can log the operator out of their own desktop.
    /// </summary>
    public static PolicyState InterpretPolicyValue(string valueName, object? rawValue)
    {
        if (string.IsNullOrEmpty(valueName))
        {
            throw new ArgumentException("The policy value name must not be empty.", nameof(valueName));
        }

        if (rawValue is null)
        {
            return PolicyState.Missing;
        }

        if (rawValue is not int numericValue)
        {
            throw new InvalidOperationException(
                $"The policy value '{valueName}' is not a DWORD, so it cannot be interpreted as consent. It must be set to a DWORD 0 to allow the operation or a non-zero DWORD to deny it.");
        }

        return numericValue == 0 ? PolicyState.Allowed : PolicyState.Denied;
    }
}