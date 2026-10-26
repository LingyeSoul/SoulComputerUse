using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SoulComputerUse.RdpCredentialExperiment;

/// <summary>One entry of the session table, read without touching anything.</summary>
internal sealed record SessionEntry(int Id, string StationName, int State);

/// <summary>
/// Reads and logs off sessions. Enumeration is read-only; the only mutating call is
/// <see cref="LogOff"/>, and it is only ever reached from a caller that proved the target is ours.
/// </summary>
internal static class SessionTable
{
    /// <summary>Enumerates every session on the machine, including the service session.</summary>
    internal static IReadOnlyList<SessionEntry> Read()
    {
        if (!WTSEnumerateSessions(0, 0, 1, out nint buffer, out int count))
        {
            int errorCode = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                errorCode,
                $"WTSEnumerateSessions failed with Win32 error {errorCode}, so the session table cannot be read.");
        }

        try
        {
            int stride = Marshal.SizeOf<WtsSessionInfo>();
            List<SessionEntry> sessions = [];
            for (int index = 0; index < count; index++)
            {
                WtsSessionInfo record = Marshal.PtrToStructure<WtsSessionInfo>(buffer + (index * stride));
                sessions.Add(new SessionEntry(record.SessionId, record.WinStationName ?? string.Empty, record.State));
            }

            return sessions;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>Formats one session for the log: id, state number and station name.</summary>
    internal static string Describe(SessionEntry session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"id={session.Id} state={session.State} station={session.StationName}");
    }

    /// <summary>Returns the session the console (the operator's own desktop) is using.</summary>
    internal static int ActiveConsoleSessionId() => (int)WTSGetActiveConsoleSessionId();

    /// <summary>Logs off one session and reports the Win32 error when the call refuses.</summary>
    internal static void LogOff(int sessionId)
    {
        if (!WTSLogoffSession(0, sessionId, false))
        {
            int errorCode = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                errorCode,
                $"WTSLogoffSession(sessionId: {sessionId}) failed with Win32 error {errorCode}.");
        }
    }

    // Exactly the three fields the API returns: id, station name and state. A record that declares a
    // field the API does not have shifts the stride, and every record after the first is then read
    // from the wrong offset — which is how a session table turns into garbage numbers.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsSessionInfo
    {
        internal int SessionId;

        [MarshalAs(UnmanagedType.LPWStr)]
        internal string WinStationName;

        internal int State;
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(nint server, int reserved, int version, out nint sessions, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSLogoffSession(nint server, int sessionId, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}
