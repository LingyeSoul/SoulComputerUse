using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Native;
using SoulComputerUse.Windows.Rdp.SessionLaunch;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// Measures the two facts the window layout depends on and that no pure function can answer: whether the
/// hosted control accepts <c>SmartSizing</c>, so the remote desktop scales into whatever room the window
/// gives it, and which window really receives the pointer over the caption and the resize border.
/// </summary>
/// <remarks>
/// A child window owns every point inside its rectangle, so the caption strip and the eight-pixel resize
/// border are only reachable while the picture stays off them. The probe reports both the parent's
/// <c>WM_NCHITTEST</c> answer and the child window that would win the point, which is the difference
/// between "the hit test is wrong" and "the hit test never runs".
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class RdpWindowChromeProbeTests
{
    private const int ClientWidth = 1200;
    private const int ClientHeight = 800;

    [TestMethod]
    public void TheControlAcceptsSmartSizingAndTheChromeStaysReachable()
    {
        ProbeResult result = RunProbe();

        Console.WriteLine(result.Describe());

        Assert.IsNull(result.Failure, "The probe must run on this machine. " + result.Describe());
        Assert.IsTrue(
            result.SmartSizingAccepted,
            "The control must accept SmartSizing, or the remote desktop cannot scale to the window and is clipped instead. "
            + result.Describe());
        Assert.IsTrue(
            result.CaptionIsTheParent,
            "A point in the caption strip must reach the product window, or the window cannot be dragged. "
            + result.Describe());
        Assert.IsTrue(
            result.LeftBorderIsTheParent,
            "A point on the left resize border must reach the product window, or the window cannot be resized. "
            + result.Describe());
        Assert.IsTrue(
            result.BottomBorderIsTheParent,
            "A point on the bottom resize border must reach the product window, or the window cannot be resized. "
            + result.Describe());
        Assert.IsTrue(
            result.PictureStartsBelowTheCaption,
            "The picture must start below the caption strip, or the control paints over the title bar. "
            + result.Describe());
    }

    private static ProbeResult RunProbe()
    {
        ProbeResult result = new();
        Thread thread = new(() =>
        {
            try
            {
                using RdpControlSurface surface = new();
                object ocx = surface.Ocx;

                result.SmartSizingAccepted = TrySmartSizing(ocx);

                using ProductWindow window = ProductWindow.Create(
                    ClientWidth,
                    ClientHeight,
                    "chrome probe",
                    () => true,
                    SessionStatusVisuals.Describe(SessionState.Starting, AuthTier.SystemCredential, GateStrength.Login, null));

                window.SetSessionSize(1280, 800);
                window.AttachPictureSurface(surface.WindowHandle, surface.Adopt);
                surface.Show();
                MessagePump.PumpFor(TimeSpan.FromMilliseconds(400), () => false);

                result.CaptionHeight = window.Metrics.Height;
                result.PictureBounds = window.PictureBounds;
                result.PictureStartsBelowTheCaption = window.PictureBounds.Top >= window.Metrics.Height;

                int captionY = Math.Max(1, window.Metrics.Height / 2);
                result.CaptionIsTheParent = IsParentAt(window, ClientWidth / 2, captionY);

                int border = CaptionMetrics.Scale(WindowFrameHitTest.ResizeBorderPixels, window.Metrics.Dpi);
                result.LeftBorderIsTheParent = IsParentAt(window, Math.Max(0, border / 2), ClientHeight / 2);
                result.BottomBorderIsTheParent = IsParentAt(window, ClientWidth / 2, ClientHeight - 1 - (border / 2));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                result.Failure = error.GetType().Name + ": " + error.Message;
            }
        })
        {
            IsBackground = true,
            Name = "SoulComputerUse chrome probe",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    /// <summary>Reports whether a client point of the product window resolves to that same window.</summary>
    /// <remarks>
    /// The hit test is sent to the product window instead of asking which window owns the screen point:
    /// a point reported by the window manager depends on whether this window happens to be in front of
    /// the terminal that runs the test, while the hit code is the window's own answer for that point.
    /// </remarks>
    private static bool IsParentAt(ProductWindow window, int x, int y)
    {
        int hitCode = window.ProbeHitTestCode(x, y);
        return hitCode is NativeMethods.HtCaption
            or NativeMethods.HtLeft
            or NativeMethods.HtRight
            or NativeMethods.HtTop
            or NativeMethods.HtBottom
            or NativeMethods.HtTopLeft
            or NativeMethods.HtTopRight
            or NativeMethods.HtBottomLeft
            or NativeMethods.HtBottomRight;
    }

    /// <summary>Writes SmartSizing, reads it back, and restores the previous value.</summary>
    private static bool TrySmartSizing(object ocx)
    {
        dynamic client = ocx;
        object previous = client.AdvancedSettings2.SmartSizing;
        client.AdvancedSettings2.SmartSizing = true;
        object readBack = client.AdvancedSettings2.SmartSizing;
        client.AdvancedSettings2.SmartSizing = previous;
        return readBack is true;
    }

    private sealed class ProbeResult
    {
        internal bool SmartSizingAccepted { get; set; }

        internal int CaptionHeight { get; set; }

        internal Rectangle PictureBounds { get; set; }

        internal bool PictureStartsBelowTheCaption { get; set; }

        internal bool CaptionIsTheParent { get; set; }

        internal bool LeftBorderIsTheParent { get; set; }

        internal bool BottomBorderIsTheParent { get; set; }

        internal string? Failure { get; set; }

        internal string Describe()
        {
            return "smartSizing=" + (SmartSizingAccepted ? "yes" : "no")
                + " captionHeight=" + CaptionHeight
                + " picture=" + PictureBounds
                + " captionReachable=" + (CaptionIsTheParent ? "yes" : "no")
                + " leftBorderReachable=" + (LeftBorderIsTheParent ? "yes" : "no")
                + " bottomBorderReachable=" + (BottomBorderIsTheParent ? "yes" : "no")
                + (Failure is null ? string.Empty : " failure=" + Failure);
        }
    }
}