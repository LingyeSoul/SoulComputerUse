using System;
using System.Runtime.InteropServices;

namespace SoulComputerUse.SessionAgent;

/// <summary>
/// Reads the window station and desktop the agent runs on. The claim gate requires the interactive
/// <c>WinSta0</c> and <c>Default</c> desktop; any other station is refused instead of being guessed.
/// </summary>
public static class WindowStationProbe
{
    private const int UserObjectName = 2;

    /// <summary>Returns true only for the interactive window station and desktop names.</summary>
    public static bool IsInteractiveName(string windowStation, string desktop)
    {
        ArgumentNullException.ThrowIfNull(windowStation);
        ArgumentNullException.ThrowIfNull(desktop);
        return string.Equals(windowStation, "WinSta0", StringComparison.Ordinal)
            && string.Equals(desktop, "Default", StringComparison.Ordinal);
    }

    /// <summary>Reads the current window station and desktop and applies the interactive check.</summary>
    /// <exception cref="InvalidOperationException">A Win32 name query failed.</exception>
    public static bool IsInteractiveDesktop()
    {
        nint windowStation = GetProcessWindowStation();
        if (windowStation == 0)
        {
            throw Failure("GetProcessWindowStation");
        }

        nint desktop = GetThreadDesktop(GetCurrentThreadId());
        if (desktop == 0)
        {
            throw Failure("GetThreadDesktop");
        }

        string stationName = ReadName(windowStation, "window station");
        string desktopName = ReadName(desktop, "desktop");
        return IsInteractiveName(stationName, desktopName);
    }

    private static string ReadName(nint handle, string objectKind)
    {
        char[] buffer = new char[256];
        if (!GetUserObjectInformationW(handle, UserObjectName, buffer, buffer.Length * 2, out int lengthNeeded))
        {
            throw Failure($"GetUserObjectInformationW ({objectKind})");
        }

        int characterCount = Math.Clamp((lengthNeeded / 2) - 1, 0, buffer.Length);
        return new string(buffer, 0, characterCount);
    }

    private static InvalidOperationException Failure(string operation)
    {
        return new InvalidOperationException(
            $"{operation} failed with Win32 error {Marshal.GetLastWin32Error()}, so the agent cannot prove it is on the interactive desktop.");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetProcessWindowStation();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetThreadDesktop(uint threadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformationW(
        nint handle,
        int index,
        [Out] char[] information,
        int length,
        out int lengthNeeded);
}