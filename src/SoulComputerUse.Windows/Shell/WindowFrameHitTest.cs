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
    /// <summary>The resize handle width in physical pixels, which is also what the spec asks for.</summary>
    public const int ResizeBorderPixels = 8;

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
}
