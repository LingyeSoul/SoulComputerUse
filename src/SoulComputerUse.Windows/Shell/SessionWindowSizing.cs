using System;
using SoulComputerUse.Windows.Native;

namespace SoulComputerUse.Windows.Shell;

/// <summary>
/// The client size a product window should be created with for one session, assembled from the
/// machine's own facts: the system DPI for the caption layout, the resize border, and the primary
/// display's work area.
/// </summary>
/// <remarks>
/// This type is the windowed layer's public entry point, so a host process needs no interop of its own;
/// <see cref="SessionFittedClientSize.Fit"/> carries the pure arithmetic and stays testable without a
/// machine.
/// </remarks>
public static class SessionWindowSizing
{
    /// <summary>
    /// What share of the session's native width the default window's picture aims for, in percent. The
    /// product's choice is forty: a window that covers the operator's whole screen is an interruption,
    /// and one much smaller makes the remote desktop unreadable, so forty percent of the primary display
    /// keeps both from happening while the session ratio still decides the window's shape.
    /// </summary>
    public const int DefaultScalePercent = 40;

    /// <summary>
    /// Computes the client size whose picture area has the session's aspect ratio at the default share
    /// of its native width, capped by the primary display's work area.
    /// </summary>
    /// <param name="sessionWidth">The session desktop width in physical pixels.</param>
    /// <param name="sessionHeight">The session desktop height in physical pixels.</param>
    public static (int Width, int Height) ClientForSession(int sessionWidth, int sessionHeight)
    {
        // The window does not exist yet, so its caption is measured from the system DPI, which is where
        // a window created at the default position lands; a later WM_DPICHANGED recomputes the layout
        // with the window's own DPI.
        int dpi = DpiProbe.GetSystemDpi();
        CaptionMetrics metrics = CaptionMetrics.Create(
            CaptionMetrics.Scale(CaptionMetrics.MinimumClientWidth, dpi) * 2,
            dpi,
            TextScaleProbe.GetTextScalePercent());
        int border = CaptionMetrics.Scale(WindowFrameHitTest.ResizeBorderPixels, dpi);
        return SessionFittedClientSize.Fit(
            sessionWidth,
            sessionHeight,
            metrics.Height,
            border,
            NativeMethods.GetSystemMetrics(NativeMethods.SmCxFullScreen),
            NativeMethods.GetSystemMetrics(NativeMethods.SmCyFullScreen),
            DefaultScalePercent);
    }
}
