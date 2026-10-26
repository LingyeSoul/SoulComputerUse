using System;
using System.Runtime.InteropServices;

namespace SoulComputerUse.Windows.Native;

/// <summary>
/// Reads the Windows Terminal Services session ids the agent's identity gate needs: the session
/// the current process runs in, and the session currently attached to the interactive console.
/// </summary>
public static class WtsSessionProbe
{
    private const uint NoConsoleSession = 0xFFFFFFFF;

    /// <summary>Returns the session id of the calling process.</summary>
    public static int GetCurrentProcessSessionId()
    {
        uint processId = (uint)Environment.ProcessId;
        if (!NativeMethods.ProcessIdToSessionId(processId, out int sessionId))
        {
            throw NativeMethods.InteropFailure($"ProcessIdToSessionId(pid={processId})", Marshal.GetLastWin32Error());
        }

        return sessionId;
    }

    /// <summary>Returns the session id attached to the interactive console.</summary>
    public static int GetActiveConsoleSessionId()
    {
        uint sessionId = NativeMethods.WTSGetActiveConsoleSessionId();
        if (sessionId == NoConsoleSession)
        {
            throw new InvalidOperationException(
                "WTSGetActiveConsoleSessionId reported that no session is attached to the console, so the identity gate cannot prove this process is not on the operator's desktop.");
        }

        return (int)sessionId;
    }
}