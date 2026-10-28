using System.Runtime.InteropServices;

namespace SoulComputerUse.Windows.Desktop;

/// <summary>
/// The terminal-services and window-station entry points the blocked-state probe needs. They live
/// in this directory so the desktop layer can be read without opening the host window's interop file.
/// </summary>
internal static class DesktopBlockNative
{
    /// <summary>The session id value that means "the calling session".</summary>
    internal const uint WtsCurrentSession = 0xFFFFFFFF;

    /// <summary>The <c>WTS_INFO_CLASS</c> value of <c>WTSConnectState</c>.</summary>
    internal const int WtsInfoClassConnectState = 8;

    /// <summary>The <c>WTS_CONNECTSTATE_CLASS</c> value of <c>WTSActive</c>, a signed-in attached user.</summary>
    internal const int WtsConnectStateActive = 0;

    /// <summary>
    /// The <c>WTS_CONNECTSTATE_CLASS</c> value of <c>WTSDisconnected</c>: the session still exists,
    /// but its client is gone and nothing it renders reaches an operator.
    /// </summary>
    internal const int WtsConnectStateDisconnected = 4;

    /// <summary>The <c>DESKTOP_READOBJECTS</c> access right.</summary>
    internal const uint DesktopReadObjects = 0x0001;

    /// <summary>The <c>DESKTOP_SWITCHDESKTOP</c> access right.</summary>
    internal const uint DesktopSwitchDesktop = 0x0100;

    /// <summary>The <c>UOI_NAME</c> information class of a window-station or desktop handle.</summary>
    internal const int UserObjectName = 2;

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSQuerySessionInformationW(
        nint server,
        uint sessionId,
        int infoClass,
        out nint buffer,
        out int byteCount);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSFreeMemory", SetLastError = true)]
    internal static extern void WTSFreeMemory(nint memory);

    [DllImport("user32.dll", EntryPoint = "OpenInputDesktop", SetLastError = true)]
    internal static extern nint OpenInputDesktop(
        uint flags,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint desiredAccess);

    [DllImport("user32.dll", EntryPoint = "CloseDesktop", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseDesktop(nint desktop);

    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetUserObjectInformationW(
        nint handle,
        int index,
        [Out] char[] information,
        int length,
        out int lengthNeeded);

    /// <summary>
    /// Returns the calling process's window station. The handle belongs to the process and must not
    /// be closed, unlike the handle <see cref="OpenInputDesktop"/> returns.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetProcessWindowStation();
}
