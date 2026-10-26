using System.Runtime.InteropServices;

namespace SoulComputerUse.Windows.Rdp.Capture;

/// <summary>
/// The user32 and gdi32 entry points the control capture needs. They live next to the capture so the
/// host window's interop file stays untouched, and they name only what this layer uses.
/// </summary>
internal static class ControlCaptureNative
{
    /// <summary>The <c>PW_RENDERFULLCONTENT</c> flag of <c>PrintWindow</c>.</summary>
    internal const uint PrintWindowRenderFullContent = 2;

    /// <summary>The <c>SRCCOPY</c> raster operation of <c>BitBlt</c>.</summary>
    internal const uint SourceCopy = 0x00CC0020;

    /// <summary>The <c>GA_ROOT</c> flag of <c>GetAncestor</c>.</summary>
    internal const uint GetAncestorRoot = 2;

    /// <summary>The <c>DIB_RGB_COLORS</c> usage value of <c>GetDIBits</c>.</summary>
    internal const uint DibRgbColors = 0;

    [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint window, out CaptureRect rectangle);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow", SetLastError = true)]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetAncestor", SetLastError = true)]
    internal static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetClassNameW(nint window, [Out] char[] className, int capacity);

    [DllImport("user32.dll", EntryPoint = "GetDC", SetLastError = true)]
    internal static extern nint GetDC(nint window);

    [DllImport("user32.dll", EntryPoint = "ReleaseDC", SetLastError = true)]
    internal static extern int ReleaseDC(nint window, nint deviceContext);

    [DllImport("user32.dll", EntryPoint = "PrintWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PrintWindow(nint window, nint deviceContext, uint flags);

    [DllImport("gdi32.dll", EntryPoint = "CreateCompatibleDC", SetLastError = true)]
    internal static extern nint CreateCompatibleDC(nint deviceContext);

    [DllImport("gdi32.dll", EntryPoint = "CreateCompatibleBitmap", SetLastError = true)]
    internal static extern nint CreateCompatibleBitmap(nint deviceContext, int width, int height);

    [DllImport("gdi32.dll", EntryPoint = "SelectObject", SetLastError = true)]
    internal static extern nint SelectObject(nint deviceContext, nint graphicObject);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint graphicObject);

    [DllImport("gdi32.dll", EntryPoint = "DeleteDC", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(nint deviceContext);

    [DllImport("gdi32.dll", EntryPoint = "BitBlt", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BitBlt(
        nint destination,
        int destinationX,
        int destinationY,
        int width,
        int height,
        nint source,
        int sourceX,
        int sourceY,
        uint rasterOperation);

    [DllImport("gdi32.dll", EntryPoint = "GetDIBits", SetLastError = true)]
    internal static extern int GetDIBits(
        nint deviceContext,
        nint bitmap,
        uint startScanLine,
        uint scanLineCount,
        [Out] byte[] bits,
        ref BitmapInfoHeader header,
        uint usage);
}

/// <summary>One window rectangle in screen pixels, as the Win32 calls fill it in.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CaptureRect
{
    internal int Left;
    internal int Top;
    internal int Right;
    internal int Bottom;

    internal readonly int Width => Right - Left;

    internal readonly int Height => Bottom - Top;
}

/// <summary>
/// The <c>BITMAPINFOHEADER</c> that describes a top-down 32-bit BGRA destination for
/// <c>GetDIBits</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BitmapInfoHeader
{
    internal uint Size;
    internal int Width;
    internal int Height;
    internal ushort Planes;
    internal ushort BitCount;
    internal uint Compression;
    internal uint ImageSize;
    internal int PixelsPerMeterX;
    internal int PixelsPerMeterY;
    internal uint ColorsUsed;
    internal uint ColorsImportant;
}
