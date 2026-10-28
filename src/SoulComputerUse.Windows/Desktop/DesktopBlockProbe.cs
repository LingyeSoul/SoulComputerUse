using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SoulComputerUse.Windows.Desktop;

/// <summary>One observation of the calling session's desktop, as the blocked guard consumes it.</summary>
/// <param name="SessionConnectState">The connection state Windows Terminal Services reports for this session.</param>
/// <param name="SessionWindowStation">The window station the calling process runs on.</param>
/// <param name="InputDesktopOpenSucceeded">True when the input desktop handle could be obtained.</param>
/// <param name="InputDesktopName">The name read from the opened input desktop, or null when it was never opened.</param>
public sealed record DesktopBlockObservation(
    DesktopSessionConnectState SessionConnectState,
    string SessionWindowStation,
    bool InputDesktopOpenSucceeded,
    string? InputDesktopName);

/// <summary>
/// Reads the facts <see cref="DesktopAvailability"/> classifies: this session's Terminal Services
/// connection state, this process's window station, and the name of the desktop that currently
/// receives input.
/// </summary>
/// <remarks>
/// Every failure other than "the input desktop did not open" is thrown. That one refusal is the
/// normal signature of a locked or protected desktop, so it is reported as data; anything else that
/// goes wrong means the facts are unknown, and an unknown fact must never be reported as usable.
/// </remarks>
public static class DesktopBlockProbe
{
    private const int NameBufferCharacters = 256;

    /// <summary>Reads one observation of the calling session's desktop.</summary>
    /// <exception cref="Win32Exception">A query the verdict depends on failed.</exception>
    /// <exception cref="InvalidOperationException">A query succeeded but returned no usable name.</exception>
    public static DesktopBlockObservation Probe()
    {
        DesktopSessionConnectState sessionConnectState = ReadSessionConnectState();
        string sessionWindowStation = ReadWindowStationName();

        nint inputDesktop = DesktopBlockNative.OpenInputDesktop(
            0,
            false,
            DesktopBlockNative.DesktopReadObjects | DesktopBlockNative.DesktopSwitchDesktop);

        if (inputDesktop == 0)
        {
            // A locked or protected desktop belongs to another logon session, so opening it is
            // refused. This is the expected answer, not a probe failure, so it is reported as data
            // and the classification turns it into the locked-desktop verdict.
            return new DesktopBlockObservation(sessionConnectState, sessionWindowStation, false, null);
        }

        bool closed;
        string inputDesktopName;
        try
        {
            inputDesktopName = ReadName(inputDesktop, "the input desktop");
        }
        finally
        {
            // The handle is released on both paths. The result is recorded instead of thrown from
            // here, because an exception raised in a finally block would replace whatever failure
            // the name query already reported.
            closed = DesktopBlockNative.CloseDesktop(inputDesktop);
        }

        if (!closed)
        {
            // A leaked desktop handle keeps the desktop and its resources alive for the lifetime of
            // the process, so the failure is surfaced instead of being treated as a usable verdict.
            throw Failure("CloseDesktop(input desktop)", Marshal.GetLastWin32Error());
        }

        return new DesktopBlockObservation(sessionConnectState, sessionWindowStation, true, inputDesktopName);
    }

    private static DesktopSessionConnectState ReadSessionConnectState()
    {
        nint buffer = 0;
        try
        {
            // A null server handle means the local machine and WTS_CURRENT_SESSION means this
            // process's own session, so no session id has to be constructed here.
            bool queried = DesktopBlockNative.WTSQuerySessionInformationW(
                0,
                DesktopBlockNative.WtsCurrentSession,
                DesktopBlockNative.WtsInfoClassConnectState,
                out buffer,
                out int byteCount);

            if (!queried)
            {
                throw Failure("WTSQuerySessionInformationW(WTSConnectState, current session)", Marshal.GetLastWin32Error());
            }

            if (buffer == 0 || byteCount < sizeof(int))
            {
                throw new InvalidOperationException(
                    $"WTSQuerySessionInformationW(WTSConnectState, current session) reported success but returned {byteCount} bytes, which cannot hold one connection state, so this session's interactivity is unknown.");
            }

            // WTS_CONNECTSTATE_CLASS is an INT-backed enumeration, so the buffer holds one 32-bit value.
            int connectState = Marshal.ReadInt32(buffer);
            return connectState switch
            {
                DesktopBlockNative.WtsConnectStateActive => DesktopSessionConnectState.Active,
                DesktopBlockNative.WtsConnectStateDisconnected => DesktopSessionConnectState.Disconnected,
                _ => DesktopSessionConnectState.Other,
            };
        }
        finally
        {
            if (buffer != 0)
            {
                DesktopBlockNative.WTSFreeMemory(buffer);
            }
        }
    }

    private static string ReadWindowStationName()
    {
        // The process window station handle is owned by the process; it is read here and never closed.
        nint windowStation = DesktopBlockNative.GetProcessWindowStation();
        if (windowStation == 0)
        {
            throw Failure("GetProcessWindowStation", Marshal.GetLastWin32Error());
        }

        return ReadName(windowStation, "the process window station");
    }

    private static string ReadName(nint handle, string objectKind)
    {
        char[] buffer = new char[NameBufferCharacters];
        if (!DesktopBlockNative.GetUserObjectInformationW(
                handle,
                DesktopBlockNative.UserObjectName,
                buffer,
                buffer.Length * sizeof(char),
                out int lengthNeeded))
        {
            throw Failure($"GetUserObjectInformationW(UOI_NAME, {objectKind})", Marshal.GetLastWin32Error());
        }

        // The returned length counts the terminating null character, and a zero length would mean the
        // object has no name at all, which no caller of this probe can classify.
        int characterCount = lengthNeeded / sizeof(char);
        if (characterCount <= 1 || characterCount > buffer.Length)
        {
            throw new InvalidOperationException(
                $"GetUserObjectInformationW(UOI_NAME, {objectKind}) succeeded but reported {lengthNeeded} bytes, which is not a usable object name, so the desktop cannot be classified.");
        }

        return new string(buffer, 0, characterCount - 1);
    }

    private static Win32Exception Failure(string operation, int errorCode)
    {
        return new Win32Exception(
            errorCode,
            $"{operation} failed with Win32 error {errorCode}, so the desktop state cannot be determined.");
    }
}
