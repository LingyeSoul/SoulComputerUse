using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using SoulComputerUse.Core.Protocol;

namespace SoulComputerUse.Windows.Rdp.Capture;

/// <summary>
/// Captures the hosted remote desktop control as PNG frames on the host side, for the session states
/// in which the child-session agent cannot produce a screenshot.
/// </summary>
/// <remarks>
/// <para>
/// The order is fixed. The control is first asked to paint itself into a memory device context with
/// <c>PrintWindow</c> and <c>PW_RENDERFULLCONTENT</c>, which is independent of whatever covers it.
/// A failed or near-black print falls back to reading the visible screen over the window rectangle,
/// but only after proving that no foreign window covers the control: a screen frame of an occluded
/// control would show the covering window and must never be passed off as the remote desktop.
/// </para>
/// <para>
/// Nothing is written to disk and nothing is logged. Error messages name operations and numeric or
/// enumerated values only, never a window title, class name, path or account.
/// </para>
/// </remarks>
public sealed class ControlFrameCapture : IControlFrameSource
{
    /// <summary>The sampling step used when judging whether a printed frame is painted at all.</summary>
    private const int BrightnessSampleStep = 8;

    private const int BytesPerPixel = 4;

    private readonly ControlCaptureOptions _options;

    /// <summary>Creates a capture with the given policy.</summary>
    /// <param name="options">The brightness, occlusion and fallback policy.</param>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    public ControlFrameCapture(ControlCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Captures one frame of the control window as encoded PNG bytes.</summary>
    /// <param name="controlHandle">The control window to read.</param>
    /// <returns>A PNG image of the control's whole window rectangle.</returns>
    /// <exception cref="ArgumentException">The handle is zero.</exception>
    /// <exception cref="Win32Exception">A Win32 call failed, including reading the window rectangle.</exception>
    /// <exception cref="InvalidOperationException">
    /// The window has no area, the printed frame cannot be used and the fallback is disabled, or the
    /// foreground window cannot be determined well enough to trust a screen frame.
    /// </exception>
    /// <exception cref="SessionOperationException">
    /// A foreign window covers the control, so a screen frame would not show the remote desktop.
    /// </exception>
    public byte[] CapturePng(nint controlHandle)
    {
        if (controlHandle == 0)
        {
            throw new ArgumentException("The control window handle must not be zero.", nameof(controlHandle));
        }

        CaptureRect rectangle = ReadWindowRectangle(controlHandle);
        int width = rectangle.Width;
        int height = rectangle.Height;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException(
                "The control window reported a "
                + width.ToString(CultureInfo.InvariantCulture)
                + "x"
                + height.ToString(CultureInfo.InvariantCulture)
                + " rectangle, so there is no frame to capture.");
        }

        PrintedFrame printed = CapturePrintedFrame(controlHandle, width, height);
        ControlCaptureDecision decision = Decide(printed, controlHandle, rectangle);
        switch (decision.Path)
        {
            case ControlCapturePath.PrintWindow:
                return EncodePng(printed.Pixels, width, height);
            case ControlCapturePath.ScreenRegion:
                return EncodePng(CaptureScreenRegion(rectangle), width, height);
            case ControlCapturePath.Refuse:
                throw CreateRefusal(decision, printed);
            default:
                throw new InvalidOperationException(
                    "The capture decision named the undefined path '" + decision.Path + "'.");
        }
    }

    /// <summary>
    /// Chooses the capture path. The printed frame wins whenever it succeeded and is bright enough;
    /// otherwise the occlusion is measured before the screen may be read, and a failed occlusion
    /// measurement refuses the frame instead of allowing it.
    /// </summary>
    private ControlCaptureDecision Decide(PrintedFrame printed, nint controlHandle, CaptureRect rectangle)
    {
        bool printedIsUsable = printed.Succeeded && printed.Brightness >= _options.MinimumBrightness;
        if (printedIsUsable)
        {
            return ControlCaptureDecision.UsePrintWindow();
        }

        if (!_options.AllowScreenRegionFallback)
        {
            return ControlCaptureDecision.DecideWithoutScreenFallback(
                printed.Succeeded,
                printed.Brightness,
                _options.MinimumBrightness);
        }

        double overlap = MeasureForegroundOverlap(controlHandle, rectangle);
        return ControlCaptureDecision.Decide(
            printed.Succeeded,
            printed.Brightness,
            _options.MinimumBrightness,
            overlap,
            _options.MaximumOverlap);
    }

    private static Exception CreateRefusal(ControlCaptureDecision decision, PrintedFrame printed)
    {
        ControlCaptureRefusal reason = decision.Refusal
            ?? throw new InvalidOperationException("The capture decision refused the frame without a defined reason.");
        return ControlCaptureFailure.ForRefusal(reason, printed.Succeeded, printed.ErrorCode);
    }

    /// <summary>
    /// Asks the control to paint itself. A print request the control rejects is reported as an
    /// unsuccessful attempt, not thrown: it is one of the facts the decision is made from.
    /// </summary>
    private static PrintedFrame CapturePrintedFrame(nint controlHandle, int width, int height)
    {
        nint windowDeviceContext = 0;
        GdiFrameBuffer? buffer = null;
        try
        {
            windowDeviceContext = ControlCaptureNative.GetDC(controlHandle);
            if (windowDeviceContext == 0)
            {
                throw Win32Failure("GetDC(control window)", Marshal.GetLastWin32Error());
            }

            buffer = GdiFrameBuffer.Create(windowDeviceContext, width, height);
            if (!ControlCaptureNative.PrintWindow(
                    controlHandle,
                    buffer.DeviceContext,
                    ControlCaptureNative.PrintWindowRenderFullContent))
            {
                return PrintedFrame.NotPrinted(Marshal.GetLastWin32Error());
            }

            byte[] pixels = buffer.ReadPixels();
            int brightness = FrameBrightness.Mean(pixels, width, height, BrightnessSampleStep);
            return PrintedFrame.Printed(pixels, brightness);
        }
        finally
        {
            buffer?.Dispose();
            if (windowDeviceContext != 0)
            {
                _ = ControlCaptureNative.ReleaseDC(controlHandle, windowDeviceContext);
            }
        }
    }

    /// <summary>Reads the visible screen over the control rectangle.</summary>
    private static byte[] CaptureScreenRegion(CaptureRect rectangle)
    {
        nint screenDeviceContext = 0;
        GdiFrameBuffer? buffer = null;
        try
        {
            screenDeviceContext = ControlCaptureNative.GetDC(0);
            if (screenDeviceContext == 0)
            {
                throw Win32Failure("GetDC(screen)", Marshal.GetLastWin32Error());
            }

            buffer = GdiFrameBuffer.Create(screenDeviceContext, rectangle.Width, rectangle.Height);
            if (!ControlCaptureNative.BitBlt(
                    buffer.DeviceContext,
                    0,
                    0,
                    rectangle.Width,
                    rectangle.Height,
                    screenDeviceContext,
                    rectangle.Left,
                    rectangle.Top,
                    ControlCaptureNative.SourceCopy))
            {
                throw Win32Failure("BitBlt(screen region)", Marshal.GetLastWin32Error());
            }

            return buffer.ReadPixels();
        }
        finally
        {
            buffer?.Dispose();
            if (screenDeviceContext != 0)
            {
                _ = ControlCaptureNative.ReleaseDC(0, screenDeviceContext);
            }
        }
    }

    /// <summary>
    /// Returns the fraction of the control covered by the foreground window. Failing to establish
    /// that window, its root, or either rectangle refuses the frame through an exception: an
    /// unmeasured overlap must never be read as "nothing covers the control".
    /// </summary>
    private static double MeasureForegroundOverlap(nint controlHandle, CaptureRect rectangle)
    {
        nint foreground = ControlCaptureNative.GetForegroundWindow();
        if (foreground == 0)
        {
            throw new InvalidOperationException(
                "The foreground window could not be determined, so the visible screen cannot be proven to show the control and the frame is refused.");
        }

        nint foregroundRoot = ControlCaptureNative.GetAncestor(foreground, ControlCaptureNative.GetAncestorRoot);
        if (foregroundRoot == 0)
        {
            throw Win32Failure("GetAncestor(foreground window)", Marshal.GetLastWin32Error());
        }

        nint controlRoot = ControlCaptureNative.GetAncestor(controlHandle, ControlCaptureNative.GetAncestorRoot);
        if (controlRoot == 0)
        {
            throw Win32Failure("GetAncestor(control window)", Marshal.GetLastWin32Error());
        }

        // The product window in front of everything means the control is visible with no cover at
        // all; the same holds for the desktop shell, which paints behind every other window.
        if (foregroundRoot == controlRoot || foregroundRoot == controlHandle)
        {
            return ControlCaptureOptions.OverlapMinimum;
        }

        if (IsDesktopShell(foregroundRoot))
        {
            return ControlCaptureOptions.OverlapMinimum;
        }

        if (!ControlCaptureNative.GetWindowRect(foregroundRoot, out CaptureRect cover))
        {
            throw Win32Failure("GetWindowRect(foreground window)", Marshal.GetLastWin32Error());
        }

        Rectangle target = Rectangle.FromLTRB(
            rectangle.Left,
            rectangle.Top,
            rectangle.Right,
            rectangle.Bottom);
        Rectangle foregroundRectangle = Rectangle.FromLTRB(cover.Left, cover.Top, cover.Right, cover.Bottom);
        return OcclusionCalculator.OverlapRatio(target, foregroundRectangle);
    }

    /// <summary>
    /// Tells whether a window is the desktop shell. Its rectangle covers the whole screen although it
    /// paints behind every other window, so counting it as a cover would refuse every frame taken
    /// while the desktop has focus. A failed read answers false, which errs toward refusing.
    /// </summary>
    private static bool IsDesktopShell(nint window)
    {
        char[] buffer = new char[256];
        int length = ControlCaptureNative.GetClassNameW(window, buffer, buffer.Length);
        if (length <= 0)
        {
            return false;
        }

        string className = new(buffer, 0, length);
        return className == "Progman" || className == "WorkerW" || className == "Shell_TrayWnd";
    }

    private static CaptureRect ReadWindowRectangle(nint controlHandle)
    {
        if (!ControlCaptureNative.GetWindowRect(controlHandle, out CaptureRect rectangle))
        {
            throw Win32Failure("GetWindowRect(control window)", Marshal.GetLastWin32Error());
        }

        return rectangle;
    }

    /// <summary>Encodes top-down BGRA pixels as a PNG without alpha, in memory.</summary>
    private static byte[] EncodePng(byte[] pixels, int width, int height)
    {
        using Bitmap bitmap = new(width, height, PixelFormat.Format32bppRgb);
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppRgb);
        try
        {
            int rowBytes = width * BytesPerPixel;
            for (int y = 0; y < height; y++)
            {
                IntPtr destination = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(pixels, y * rowBytes, destination, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        using MemoryStream stream = new();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static Win32Exception Win32Failure(string operation, int errorCode)
    {
        return new Win32Exception(
            errorCode,
            operation
            + " failed with Win32 error "
            + errorCode.ToString(CultureInfo.InvariantCulture)
            + ".");
    }

    /// <summary>
    /// The outcome of one self-paint attempt: whether the control printed, the pixels it printed,
    /// the mean brightness of those pixels, and the Win32 code a rejected print reported.
    /// </summary>
    private readonly record struct PrintedFrame(bool Succeeded, byte[] Pixels, int Brightness, int ErrorCode)
    {
        internal static PrintedFrame Printed(byte[] pixels, int brightness)
        {
            return new PrintedFrame(true, pixels, brightness, 0);
        }

        internal static PrintedFrame NotPrinted(int errorCode)
        {
            return new PrintedFrame(false, [], 0, errorCode);
        }
    }
}
