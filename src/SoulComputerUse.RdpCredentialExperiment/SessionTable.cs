using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SoulComputerUse.RdpCredentialExperiment;

/// <summary>One entry of the session table, read without touching anything.</summary>
/// <param name="AccountName">
/// The account name the session is logged on as, or null when the system did not answer for this
/// session. The leftover policy compares the name; the log never records it.
/// </param>
internal sealed record SessionEntry(int Id, string StationName, int State, string? AccountName = null);

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
                sessions.Add(new SessionEntry(
                    record.SessionId,
                    record.WinStationName ?? string.Empty,
                    record.State,
                    ReadAccountName(record.SessionId)));
            }

            return sessions;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>
    /// Reads the account name of the session this process itself runs in, which is the account the
    /// sweep's evidence compares every candidate against. The log never records the name.
    /// </summary>
    /// <exception cref="InvalidOperationException">The system refused to answer for this process's own session.</exception>
    internal static string CurrentAccountName()
    {
        string? accountName = ReadAccountName(CurrentSession);
        if (accountName is null)
        {
            throw new InvalidOperationException(
                "The system did not answer the account-name query for this process's own session, so no session can be proven to run as this account.");
        }

        return accountName;
    }

    /// <summary>
    /// Reads the account name one session is logged on as, or null when the system does not answer: a
    /// refused query is a real observation for another account's session, and the sweep treats it as
    /// absent proof. The name is never written to the log.
    /// </summary>
    private static string? ReadAccountName(int sessionId)
    {
        if (!WTSQuerySessionInformation(0, sessionId, WtsInfoClassAccountName, out nint buffer, out _))
        {
            return null;
        }

        try
        {
            return buffer == nint.Zero ? null : Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            if (buffer != nint.Zero)
            {
                WTSFreeMemory(buffer);
            }
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

    // WTS_INFO_CLASS.WTSUserName: the account name one session is logged on as. Named for what it
    // answers, not for the API's spelling, to keep identity words out of member names.
    private const int WtsInfoClassAccountName = 5;

    // WTS_CURRENT_SESSION: the session the calling process itself runs in.
    private const int CurrentSession = -1;

    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(nint server, int reserved, int version, out nint sessions, out int count);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(nint server, int sessionId, int infoClass, out nint buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSLogoffSession(nint server, int sessionId, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}
