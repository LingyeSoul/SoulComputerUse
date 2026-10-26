using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SoulComputerUse.Windows.Hosting.Startup.Split;

/// <summary>
/// Reads whether a Windows Terminal Services session already has a logged-on user, which is the only
/// machine-level proof that some operator completed a logon in it.
/// </summary>
/// <remarks>
/// <para>
/// The client reports <c>3</c> while its window shows the session's own Windows logon screen, but a client
/// that asks for the credentials itself authenticates before the session paints that screen: the state then
/// goes from connecting straight to connected. In that case the session's user name is the remaining
/// evidence that a logon really happened, and it appears only after Windows accepted a credential.
/// </para>
/// <para>
/// This type never reads the user name into a message, a log or an exception: the name is compared against
/// empty and discarded. It answers a question, not a value.
/// </para>
/// </remarks>
public static class WtsSessionUserQuery
{
    private const int WtsInfoClassSessionInfo = 24;
    private const int WtsInfoClassUserName = 5;
    private const int StationNameLength = 32;
    private const int DomainLength = 17;
    private const int UserNameLength = 21;

    /// <summary>
    /// Reports whether any session other than the service session and the operator's console session has a
    /// logged-on user. The console session is excluded because its own user is always present and would
    /// answer the question about the child session with the operator's own logon.
    /// </summary>
    /// <param name="consoleSessionId">The session attached to the operator's console.</param>
    /// <exception cref="ArgumentOutOfRangeException">The console session id is negative.</exception>
    /// <exception cref="Win32Exception">A session could not be inspected.</exception>
    public static bool AnyChildSessionHasLoggedOnUser(int consoleSessionId)
    {
        if (consoleSessionId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(consoleSessionId),
                consoleSessionId,
                "A Windows session id cannot be negative.");
        }

        foreach (int sessionId in EnumerateSessionIds())
        {
            if (sessionId == 0 || sessionId == consoleSessionId)
            {
                continue;
            }

            if (HasLoggedOnUser(sessionId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reports whether one session reports a non-empty user name.</summary>
    /// <exception cref="Win32Exception">The session could not be queried.</exception>
    public static bool HasLoggedOnUser(int sessionId)
    {
        if (sessionId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionId), sessionId, "A Windows session id cannot be negative.");
        }

        string? userName = QueryString(sessionId, WtsInfoClassUserName);
        return !string.IsNullOrEmpty(userName);
    }

    private static List<int> EnumerateSessionIds()
    {
        if (!WTSEnumerateSessionsW(0, 0, 1, out nint buffer, out int count))
        {
            int errorCode = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                errorCode,
                $"WTSEnumerateSessions failed with Win32 error {errorCode}, so the session table cannot be read.");
        }

        try
        {
            List<int> sessionIds = new(count);
            int stride = Marshal.SizeOf<WtsSessionInfo>();
            for (int index = 0; index < count; index++)
            {
                WtsSessionInfo record = Marshal.PtrToStructure<WtsSessionInfo>(buffer + (index * stride));
                sessionIds.Add(record.SessionId);
            }

            return sessionIds;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static string? QueryString(int sessionId, int infoClass)
    {
        if (!WTSQuerySessionInformationW(0, sessionId, infoClass, out nint buffer, out int byteCount))
        {
            int errorCode = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                errorCode,
                $"WTSQuerySessionInformation failed with Win32 error {errorCode} for one session, so its logon state cannot be read.");
        }

        try
        {
            return byteCount <= 0 ? null : Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    // The API returns exactly these three fields; a record that declares more shifts the stride and every
    // later entry is read from the wrong offset.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsSessionInfo
    {
        internal int SessionId;

        [MarshalAs(UnmanagedType.LPWStr)]
        internal string? WinStationName;

        internal int State;
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(nint server, int reserved, int version, out nint sessions, out int count);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(nint server, int sessionId, int infoClass, out nint buffer, out int byteCount);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);
}