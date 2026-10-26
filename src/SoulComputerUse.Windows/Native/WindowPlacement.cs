using System;
using System.Runtime.InteropServices;

namespace SoulComputerUse.Windows.Native;

/// <summary>
/// Places a top-level window in physical screen pixels. Used by the M0 comparison windows so the
/// two of them never overlap while a human is looking at their caption buttons.
/// </summary>
public static class WindowPlacement
{
    /// <summary>Moves and resizes a window without changing its z-order or activating it.</summary>
    public static void MoveAndResize(nint handle, int screenX, int screenY, int width, int height)
    {
        if (handle == 0)
        {
            throw new ArgumentException("The window handle must not be zero.", nameof(handle));
        }

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "The width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "The height must be positive.");
        }

        bool moved = NativeMethods.SetWindowPos(
            handle,
            0,
            screenX,
            screenY,
            width,
            height,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        if (!moved)
        {
            throw NativeMethods.InteropFailure(
                $"SetWindowPos(hwnd=0x{handle:X}, x={screenX}, y={screenY}, w={width}, h={height})",
                Marshal.GetLastWin32Error());
        }
    }
}