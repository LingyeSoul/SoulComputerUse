using System;
using System.Globalization;
using Microsoft.Win32;

namespace SoulComputerUse.Windows.Rdp;

/// <summary>
/// Confirms the machine is in the state the credential experiment requires before anything connects.
/// </summary>
/// <remarks>
/// A local RDP connection made without the child-session setting takes over the console session and
/// logs the operator out, so every precondition is checked up front and a missing or surprising value
/// refuses the experiment instead of being assumed harmless.
/// </remarks>
public static class RdpExperimentPreconditions
{
    /// <summary>The value that lets remote desktop connections in.</summary>
    public const int DeniedConnectionsValue = 0;

    /// <summary>The value that lets child-session connections in.</summary>
    public const int DeniedChildConnectionsValue = 0;

    /// <summary>Checks the machine and returns the facts it proved, or throws with the reason it refused.</summary>
    public static RdpExperimentPreconditionsResult Check(
        bool childSessionsEnabled,
        object? denyTsConnectionsValue,
        object? denyChildConnectionsValue)
    {
        if (!childSessionsEnabled)
        {
            throw new InvalidOperationException(
                "The child-session system setting is off, so a connection would land in the operator's own session; the experiment is refused.");
        }

        int denyTs = ReadDword(denyTsConnectionsValue, "fDenyTSConnections");
        if (denyTs != DeniedConnectionsValue)
        {
            throw new InvalidOperationException(
                $"fDenyTSConnections is {denyTs}, so remote connections are refused by policy and the experiment cannot run.");
        }

        int denyChild = ReadDword(denyChildConnectionsValue, "fDenyChildConnections");
        if (denyChild != DeniedChildConnectionsValue)
        {
            throw new InvalidOperationException(
                $"fDenyChildConnections is {denyChild}, so child-session connections are refused by policy and the experiment cannot run; a plain connection would take over the operator's session.");
        }

        return new RdpExperimentPreconditionsResult(denyTs, denyChild);
    }

    private static int ReadDword(object? rawValue, string name)
    {
        if (rawValue is null)
        {
            throw new InvalidOperationException(
                $"{name} is missing, and a missing policy value is treated as refusal rather than as a default.");
        }

        if (rawValue is not int value)
        {
            throw new InvalidOperationException(
                $"{name} is a {rawValue.GetType().Name}, but a DWORD was expected.");
        }

        return value;
    }

    /// <summary>
    /// Reads the policy value from the terminal server key. The raw value is returned unchanged so the
    /// caller can tell a missing value apart from a value of the wrong type: both refuse the
    /// connection, but the operator fixes them differently.
    /// </summary>
    public static object? ReadPolicyValue(string valueName)
    {
        ArgumentException.ThrowIfNullOrEmpty(valueName);

        return Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server", valueName, null);
    }

    /// <summary>Formats the checked facts for a log line, without any machine-specific data.</summary>
    public static string Describe(RdpExperimentPreconditionsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"childSessions=on fDenyTSConnections={result.DenyTsConnections} fDenyChildConnections={result.DenyChildConnections}");
    }
}

/// <summary>The facts a precondition check proved.</summary>
public sealed record RdpExperimentPreconditionsResult(int DenyTsConnections, int DenyChildConnections);
