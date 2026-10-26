using System;
using System.Runtime.InteropServices;
using SoulComputerUse.Windows.Native;

namespace SoulComputerUse.Windows.Identity;

/// <summary>The connection states Windows Terminal Services reports for a session.</summary>
internal enum WtsConnectState
{
    /// <summary>A user is signed in and the session is attached.</summary>
    Active = 0,

    /// <summary>A client is connected but the session is not active yet.</summary>
    Connected = 1,

    /// <summary>The session is looking for a client.</summary>
    ConnectQuery = 2,

    /// <summary>The session is being shadowed.</summary>
    Shadow = 3,

    /// <summary>A user is signed in but the session is not attached.</summary>
    Disconnected = 4,

    /// <summary>The session is waiting for a client.</summary>
    Idle = 5,

    /// <summary>The session is listening for a client.</summary>
    Listen = 6,

    /// <summary>The session is being reset.</summary>
    Reset = 7,

    /// <summary>The session is down.</summary>
    Down = 8,

    /// <summary>The session is being initialized.</summary>
    Init = 9,
}

/// <summary>
/// The fixed part of <c>WTSINFOW</c> for one session, in the layout the Windows SDK defines and
/// Windows fills in. Marshalling is positional, so the member names are free to avoid spelling
/// account identity words in product sources.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WtsSessionInfo
{
    // WINSTATIONNAME_LENGTH, DOMAIN_LENGTH and USERNAME_LENGTH + 1 from wtsapi32.h.
    private const int StationNameLength = 32;
    private const int AccountDomainLength = 17;
    private const int AccountNameLength = 21;

    internal WtsConnectState ConnectState;
    internal int SessionId;
    internal int IncomingBytes;
    internal int OutgoingBytes;
    internal int IncomingFrames;
    internal int OutgoingFrames;
    internal int IncomingCompressedBytes;
    internal int OutgoingCompressedBytes;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = StationNameLength)]
    internal string StationName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AccountDomainLength)]
    internal string AccountDomain;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AccountNameLength)]
    internal string AccountName;

    internal long ConnectTime;
    internal long DisconnectTime;
    internal long LastInputTime;
    internal long LogonTime;
    internal long CurrentTime;
}

/// <summary>
/// Reads whether the current Windows Terminal Services session carries an interactive logon. This
/// is the machine fact behind the system-credential tier of section 7.2: the session must be active,
/// and it must hold a signed-in account. Account values themselves are only tested for emptiness and
/// are never reported, logged or copied anywhere.
/// </summary>
public static class InteractiveLogonProbe
{
    /// <summary>
    /// Returns true only when Windows reports the current session as active and holding a signed-in
    /// account. A failed query throws instead of returning false: an unreadable fact must surface,
    /// because a silent false would turn a broken probe into a confident "no interaction" verdict.
    /// </summary>
    public static bool HasInteractiveLogonToken()
    {
        nint buffer = 0;
        try
        {
            // WTS_CURRENT_SERVER_HANDLE is NULL and means the local machine.
            bool queried = NativeMethods.WTSQuerySessionInformationW(
                0,
                NativeMethods.WtsCurrentSession,
                NativeMethods.WtsInfoClassSessionInfo,
                out buffer,
                out int byteCount);

            if (!queried)
            {
                int errorCode = Marshal.GetLastWin32Error();
                throw NativeMethods.InteropFailure(
                    "WTSQuerySessionInformationW(WTSSessionInfo, current session)",
                    errorCode);
            }

            if (buffer == 0)
            {
                throw new InvalidOperationException(
                    "WTSQuerySessionInformationW(WTSSessionInfo, current session) reported success but returned no buffer, so this session's logon state cannot be read.");
            }

            int expectedSize = Marshal.SizeOf<WtsSessionInfo>();
            if (byteCount < expectedSize)
            {
                throw new InvalidOperationException(
                    $"WTSQuerySessionInformationW(WTSSessionInfo, current session) succeeded but returned {byteCount} bytes, which is smaller than the {expectedSize}-byte session information structure, so this session's logon state cannot be read.");
            }

            WtsSessionInfo session = Marshal.PtrToStructure<WtsSessionInfo>(buffer);
            if (!Enum.IsDefined(session.ConnectState))
            {
                throw new InvalidOperationException(
                    $"WTSQuerySessionInformationW(WTSSessionInfo, current session) returned connection state {(int)session.ConnectState}, which this probe does not define, so this session's logon state cannot be classified.");
            }

            return session.ConnectState == WtsConnectState.Active
                && !string.IsNullOrEmpty(session.AccountName)
                && !string.IsNullOrEmpty(session.AccountDomain);
        }
        finally
        {
            if (buffer != 0)
            {
                NativeMethods.WTSFreeMemory(buffer);
            }
        }
    }
}
