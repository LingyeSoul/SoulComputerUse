using System;
using SoulComputerUse.Windows.Native;

namespace SoulComputerUse.Windows.Shell;

/// <summary>Which resize handle a point on the window frame lands on.</summary>
public enum ResizeEdge
{
    /// <summary>Not on a resize handle.</summary>
    None = 0,

    /// <summary>The left edge.</summary>
    Left = 1,

    /// <summary>The right edge.</summary>
    Right = 2,

    /// <summary>The top edge.</summary>
    Top = 3,

    /// <summary>The bottom edge.</summary>
    Bottom = 4,

    /// <summary>The top-left corner.</summary>
    TopLeft = 5,

    /// <summary>The top-right corner.</summary>
    TopRight = 6,

    /// <summary>The bottom-left corner.</summary>
    BottomLeft = 7,

    /// <summary>The bottom-right corner.</summary>
    BottomRight = 8,
}

/// <summary>
/// The resize handles of a window that has no non-client area. With <c>WM_NCCALCSIZE</c> answering
/// zero the system draws no frame, and then nothing is resizable unless the window procedure hands
/// out the edge hit codes itself; without this the window becomes a fixed-size picture.
/// </summary>
public static class WindowFrameHitTest
{
    /// <summary>
    /// The resize handle width in physical pixels, which is also what the spec asks for.
    /// </summary>
    /// <remarks>
    /// Four logical pixels: wide enough to grab at every DPI, and narrow enough that the band it keeps
    /// free around the remote desktop reads as a frame hairline rather than a black bar. A child window
    /// owns every point inside its rectangle, so this band is also what keeps the eight resize handles
    /// reachable at all — zero would return an unresizable window.
    /// </remarks>
    public const int ResizeBorderPixels = 4;

    /// <summary>Resolves the resize handle under a client-area point.</summary>
    /// <param name="clientWidth">Client width in physical pixels.</param>
    /// <param name="clientHeight">Client height in physical pixels.</param>
    /// <param name="borderPixels">Resize handle width in physical pixels.</param>
    /// <param name="x">Point x in client coordinates.</param>
    /// <param name="y">Point y in client coordinates.</param>
    public static ResizeEdge ResolveResizeEdge(int clientWidth, int clientHeight, int borderPixels, int x, int y)
    {
        if (clientWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clientWidth), clientWidth, "The client width must be positive.");
        }

        if (clientHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clientHeight), clientHeight, "The client height must be positive.");
        }

        if (borderPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(borderPixels), borderPixels, "The resize handle width must be positive.");
        }

        if (x < 0 || x >= clientWidth || y < 0 || y >= clientHeight)
        {
            return ResizeEdge.None;
        }

        bool onLeft = x < borderPixels;
        bool onRight = x >= clientWidth - borderPixels;
        bool onTop = y < borderPixels;
        bool onBottom = y >= clientHeight - borderPixels;

        if (onTop)
        {
            if (onLeft)
            {
                return ResizeEdge.TopLeft;
            }

            return onRight ? ResizeEdge.TopRight : ResizeEdge.Top;
        }

        if (onBottom)
        {
            if (onLeft)
            {
                return ResizeEdge.BottomLeft;
            }

            return onRight ? ResizeEdge.BottomRight : ResizeEdge.Bottom;
        }

        if (onLeft)
        {
            return ResizeEdge.Left;
        }

        return onRight ? ResizeEdge.Right : ResizeEdge.None;
    }

    /// <summary>Returns the <c>WM_NCHITTEST</c> code of a resize handle.</summary>
    public static int ToHitTestCode(ResizeEdge edge)
    {
        return edge switch
        {
            ResizeEdge.None => NativeMethods.HtClient,
            ResizeEdge.Left => NativeMethods.HtLeft,
            ResizeEdge.Right => NativeMethods.HtRight,
            ResizeEdge.Top => NativeMethods.HtTop,
            ResizeEdge.Bottom => NativeMethods.HtBottom,
            ResizeEdge.TopLeft => NativeMethods.HtTopLeft,
            ResizeEdge.TopRight => NativeMethods.HtTopRight,
            ResizeEdge.BottomLeft => NativeMethods.HtBottomLeft,
            ResizeEdge.BottomRight => NativeMethods.HtBottomRight,
            _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, "Unknown resize handle."),
        };
    }

    /// <summary>
    /// The <c>WM_SYSCOMMAND</c> wParam that starts the system's resize loop for one resize handle's hit
    /// code.
    /// </summary>
    /// <remarks>
    /// <c>SC_SIZE</c> carries the edge in its low four bits, and those codes run from
    /// <c>WMSZ_LEFT</c> (1) to <c>WMSZ_BOTTOMRIGHT</c> (8) in the same order as the <c>HTLEFT</c> family.
    /// The family starts at ten, not at one, so the offset is taken from <c>HTLEFT</c>; an offset from
    /// <c>HTCAPTION</c> would shift every edge by seven and push half of them past the four-bit domain
    /// the resize loop accepts, which the default procedure answers by refusing the command: the window
    /// then cannot be resized at all.
    /// </remarks>
    /// <param name="hitTestCode">The hit code of the pressed resize handle.</param>
    public static int SystemSizeCommand(int hitTestCode)
    {
        return NativeMethods.ScSize + hitTestCode - NativeMethods.HtLeft + NativeMethods.WmszLeft;
    }
}
