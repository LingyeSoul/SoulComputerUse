using System.Runtime.InteropServices;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>
/// The Terminal Services entry points the teardown layer needs. They live in this directory instead
/// of the host window's interop file so the teardown surface can be read in one place.
/// </summary>
internal static class WtsSessionNative
{
    /// <summary>The value <c>WTSGetActiveConsoleSessionId</c> returns when no console session exists.</summary>
    internal const uint NoConsoleSession = 0xFFFFFFFF;

    /// <summary>
    /// One <c>WTS_SESSION_INFOW</c> record.
    /// </summary>
    /// <remarks>
    /// Exactly the three fields the API returns: the session id, the station name and the state. A
    /// record that declared any extra field would change the marshalled size, the stride used to walk
    /// the returned array would be wrong, and every entry after the first would be read from the wrong
    /// offset. The count comes from the API, not from this struct, so the extra field would not even be
    /// detected as a mismatch: it would silently turn the session table into garbage ids.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WtsSessionInfo
    {
        internal int SessionId;

        [MarshalAs(UnmanagedType.LPWStr)]
        internal string WinStationName;

        internal int State;
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSEnumerateSessionsW(
        nint server,
        int reserved,
        int version,
        out nint sessionInfo,
        out int count);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSFreeMemory")]
    internal static extern void WTSFreeMemory(nint memory);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSLogoffSession", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSLogoffSession(
        nint server,
        int sessionId,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    /// <summary>
    /// <c>WTS_INFO_CLASS.WTSUserName</c>: the account name one session is logged on as. The constant is
    /// named for what it answers rather than for the API's own spelling, because the product's sources
    /// keep identity words out of member names.
    /// </summary>
    internal const int WtsInfoClassAccountName = 5;

    /// <summary><c>WTS_CURRENT_SESSION</c>: the session the calling process itself runs in.</summary>
    internal const int CurrentSession = -1;

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSQuerySessionInformationW(
        nint server,
        int sessionId,
        int infoClass,
        out nint buffer,
        out int bytesReturned);

    [DllImport("kernel32.dll", EntryPoint = "WTSGetActiveConsoleSessionId")]
    internal static extern uint WTSGetActiveConsoleSessionId();
}
