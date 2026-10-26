using System;
using SoulComputerUse.Windows.Native;

namespace SoulComputerUse.Windows.Shell;

/// <summary>The parts of the self-drawn caption a point can land on.</summary>
public enum CaptionPart
{
    /// <summary>Below the caption strip: the desktop picture.</summary>
    None = 0,

    /// <summary>The caption strip itself, excluding the three buttons.</summary>
    Caption = 1,

    /// <summary>The minimize button.</summary>
    Minimize = 2,

    /// <summary>The maximize or restore button.</summary>
    Maximize = 3,

    /// <summary>The close button.</summary>
    Close = 4,
}

/// <summary>
/// Maps a point to a caption part and to the <c>WM_NCHITTEST</c> code the window procedure answers
/// with. The mapping is pure so the hit zones can be checked without a window.
/// </summary>
/// <remarks>
/// The three buttons answer their system hit codes, so the window manager itself performs minimize,
/// maximize and close; the Snap Layouts flyout is not promised because this machine does not show it
/// for a self-drawn hot zone (see the title bar spec), but the maximize action itself is unchanged.
/// The caption strip answers <c>HTCAPTION</c>, which keeps dragging and double-click-to-maximize
/// working through the default window procedure.
/// </remarks>
public static class CaptionHitTest
{
    /// <summary>Resolves the caption part under a client-area point.</summary>
    public static CaptionPart Resolve(CaptionMetrics metrics, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        if (x < 0 || x >= metrics.ClientWidth || y < 0 || y >= metrics.Height)
        {
            return CaptionPart.None;
        }

        if (metrics.CloseBounds.Contains(x, y))
        {
            return CaptionPart.Close;
        }

        if (metrics.MaximizeBounds.Contains(x, y))
        {
            return CaptionPart.Maximize;
        }

        if (metrics.MinimizeBounds.Contains(x, y))
        {
            return CaptionPart.Minimize;
        }

        return CaptionPart.Caption;
    }

    /// <summary>
    /// Returns the <c>WM_NCHITTEST</c> code for a caption part. The three buttons answer the system
    /// codes, so the window manager performs the action itself and the same command arrives no matter
    /// which of the four close paths was used.
    /// </summary>
    public static int ToHitTestCode(CaptionPart part)
    {
        return part switch
        {
            CaptionPart.None => NativeMethods.HtClient,
            CaptionPart.Caption => NativeMethods.HtCaption,
            CaptionPart.Minimize => NativeMethods.HtMinButton,
            CaptionPart.Maximize => NativeMethods.HtMaxButton,
            CaptionPart.Close => NativeMethods.HtClose,
            _ => throw new ArgumentOutOfRangeException(nameof(part), part, "Unknown caption part."),
        };
    }

    /// <summary>Returns the caption button a system hit-test code denotes, or null when it is none.</summary>
    public static CaptionButtonKind? ButtonFromHitTestCode(int hitTestCode)
    {
        return hitTestCode switch
        {
            NativeMethods.HtMinButton => CaptionButtonKind.Minimize,
            NativeMethods.HtMaxButton => CaptionButtonKind.Maximize,
            NativeMethods.HtClose => CaptionButtonKind.Close,
            _ => null,
        };
    }

    /// <summary>Tells whether the part is one of the three caption buttons.</summary>
    public static bool IsButton(CaptionPart part)
    {
        return part is CaptionPart.Minimize or CaptionPart.Maximize or CaptionPart.Close;
    }

    /// <summary>Returns the rectangle one caption button occupies.</summary>
    public static System.Drawing.Rectangle BoundsOf(CaptionMetrics metrics, CaptionPart part)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        return part switch
        {
            CaptionPart.Minimize => metrics.MinimizeBounds,
            CaptionPart.Maximize => metrics.MaximizeBounds,
            CaptionPart.Close => metrics.CloseBounds,
            _ => throw new ArgumentOutOfRangeException(nameof(part), part, "Only the three caption buttons have a rectangle."),
        };
    }

    /// <summary>Returns the button a caption part denotes, or null when it is not a button.</summary>
    public static CaptionButtonKind? ToButtonKind(CaptionPart part)
    {
        return part switch
        {
            CaptionPart.Minimize => CaptionButtonKind.Minimize,
            CaptionPart.Maximize => CaptionButtonKind.Maximize,
            CaptionPart.Close => CaptionButtonKind.Close,
            CaptionPart.None or CaptionPart.Caption => null,
            _ => throw new ArgumentOutOfRangeException(nameof(part), part, "Unknown caption part."),
        };
    }

    /// <summary>Returns the caption part one of the three buttons occupies.</summary>
    public static CaptionPart ToCaptionPart(CaptionButtonKind kind)
    {
        return kind switch
        {
            CaptionButtonKind.Minimize => CaptionPart.Minimize,
            CaptionButtonKind.Maximize => CaptionPart.Maximize,
            CaptionButtonKind.Close => CaptionPart.Close,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown caption button."),
        };
    }
}
