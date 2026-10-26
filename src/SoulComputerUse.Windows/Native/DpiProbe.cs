using System;
using System.Runtime.InteropServices;

namespace SoulComputerUse.Windows.Native;

/// <summary>How much DPI awareness the calling thread has.</summary>
public enum DpiAwareness
{
    /// <summary>The thread sees virtualized, scaled coordinates.</summary>
    Unaware = 0,

    /// <summary>The thread sees physical pixels of the system DPI only.</summary>
    SystemAware = 1,

    /// <summary>The thread sees physical pixels of the monitor the window is on.</summary>
    PerMonitorAware = 2,
}

/// <summary>
/// Reports the DPI facts a caption hit test depends on. A process without DPI awareness gets
/// virtualized coordinates, and then a "correct" hit test lands on the wrong pixels.
/// </summary>
public static class DpiProbe
{
    /// <summary>Returns the DPI of the monitor the window is on.</summary>
    public static int GetWindowDpi(nint handle)
    {
        uint dpi = NativeMethods.GetDpiForWindow(handle);
        if (dpi == 0)
        {
            throw NativeMethods.InteropFailure($"GetDpiForWindow(hwnd=0x{handle:X})", Marshal.GetLastWin32Error());
        }

        return (int)dpi;
    }

    /// <summary>Returns the DPI awareness of the calling thread.</summary>
    public static DpiAwareness GetThreadAwareness()
    {
        nint context = NativeMethods.GetThreadDpiAwarenessContext();
        int awareness = NativeMethods.GetAwarenessFromDpiAwarenessContext(context);
        if (awareness < (int)DpiAwareness.Unaware || awareness > (int)DpiAwareness.PerMonitorAware)
        {
            throw new InvalidOperationException(
                $"GetAwarenessFromDpiAwarenessContext returned {awareness}, which is not a known DPI awareness value.");
        }

        return (DpiAwareness)awareness;
    }

    /// <summary>Reads both facts and formats them for display, for example "DPI 144 · 感知 PerMonitorAware".</summary>
    public static string Describe(nint handle)
    {
        int dpi = GetWindowDpi(handle);
        DpiAwareness awareness = GetThreadAwareness();
        return $"DPI {dpi} · 感知 {awareness}";
    }
}