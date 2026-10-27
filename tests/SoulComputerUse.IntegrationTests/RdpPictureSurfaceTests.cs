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
/// Proves the picture path the operator looks at: once the product window adopts the remote desktop
/// control, the control must be a visible child window sized exactly on the picture rectangle. A control
/// left invisible, unsized or off the rectangle shows a black client area while the session is live, which
/// looks exactly like a session that never connected.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RdpPictureSurfaceTests
{
    private const int WindowWidth = 1000;
    private const int WindowHeight = 700;

    [TestMethod]
    public void TheAdoptedControlIsAVisibleChildOnThePictureBounds()
    {
        PictureProbe probe = RunProbe();

        Assert.IsNull(probe.Failure, "The picture path must run on this machine without a failure.");
        Assert.AreEqual(probe.ControlHandle, probe.AttachedSurface, "The product window must adopt the control's own handle.");
        Assert.IsTrue(probe.StyleHasChild, "The control must be a child of the product window, or it floats over the screen.");
        Assert.IsTrue(probe.StyleHasVisible, "The control must carry the visible style, or the client area stays black.");
        Assert.IsFalse(probe.PictureBounds.IsEmpty, "The session size must produce a picture rectangle inside the client area.");

        Assert.AreEqual(
            probe.PictureBounds,
            probe.ControlRectangle,
            "The control must cover the picture rectangle exactly after a session size change; a letterbox must never be painted over. "
            + $"picture={probe.PictureBounds}, afterAttach={probe.AfterAttach}, afterShow={probe.AfterShow}, "
            + $"afterPump={probe.AfterPump}, afterResize={probe.AfterResize}, afterSettledLayout={probe.AfterSettledLayout}.");
        Assert.AreEqual(
            NativeMethods.HtCaption,
            probe.CaptionHitAfterSettle,
            "A point in the caption strip must still hit the caption after the hosted layout settled, or the window cannot be dragged or closed. "
            + $"control={probe.AfterSettledLayout}, picture={probe.PictureBounds}.");
    }

    private static int ClientCenter(ProductWindow window)
    {
        if (!NativeMethods.GetClientRect(window.Handle, out NativeRect client))
        {
            throw new InvalidOperationException(
                $"GetClientRect failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        return (client.Right - client.Left) / 2;
    }

    private static PictureProbe RunProbe()
    {
        PictureProbe probe = new();
        Thread thread = new(() =>
        {
            try
            {
                using RdpControlSurface surface = new();
                using ProductWindow window = ProductWindow.Create(
                    WindowWidth,
                    WindowHeight,
                    "picture probe",
                    () => true,
                    SessionStatusVisuals.Describe(SessionState.Starting, AuthTier.SystemCredential, GateStrength.Login, null));

                window.SetSessionSize(1280, 800);
                window.AttachPictureSurface(surface.WindowHandle, surface.Adopt);
                probe.AfterAttach = RectangleOf(surface.WindowHandle);
                surface.Show();
                probe.AfterShow = RectangleOf(surface.WindowHandle);
                MessagePump.PumpFor(TimeSpan.FromMilliseconds(500), () => false);
                probe.AfterPump = RectangleOf(surface.WindowHandle);

                window.SetSessionSize(800, 600);
                probe.ResizedPictureBounds = window.PictureBounds;
                probe.AfterResize = RectangleOf(surface.WindowHandle);

                // The deferred layout is what actually breaks the caption: Windows Forms re-places the
                // control against the carrier it believes is still its parent, so the control can jump to
                // the product window's origin and cover the title bar and resize border.
                MessagePump.PumpFor(TimeSpan.FromMilliseconds(600), () => false);
                probe.AfterSettledLayout = RectangleInWindowCoordinates(surface.WindowHandle, window.Handle);
                probe.CaptionHitAfterSettle = window.ProbeHitTestCode(ClientCenter(window), window.Metrics.Height / 2);

                probe.ControlHandle = surface.WindowHandle;
                probe.AttachedSurface = window.PictureSurface;
                probe.PictureBounds = probe.ResizedPictureBounds;
                probe.StyleHasChild = HasStyle(surface.WindowHandle, NativeMethods.WsChild);
                probe.StyleHasVisible = HasStyle(surface.WindowHandle, (long)NativeMethods.WsVisible);
                probe.ControlRectangle = probe.AfterSettledLayout;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                probe.Failure = error.GetType().Name + ": " + error.Message;
            }
        })
        {
            IsBackground = true,
            Name = "SoulComputerUse picture probe",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return probe;
    }

    /// <summary>Reads a child window's rectangle in its parent's client coordinates.</summary>
    private static Rectangle RectangleInWindowCoordinates(nint handle, nint parent)
    {
        Rectangle rectangle = RectangleOf(handle);
        NativePoint origin = new()
        {
            X = rectangle.Left,
            Y = rectangle.Top,
        };
        if (!NativeMethods.ScreenToClient(parent, ref origin))
        {
            throw new InvalidOperationException(
                $"ScreenToClient failed for handle {handle.ToInt64()} with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        return Rectangle.FromLTRB(origin.X, origin.Y, origin.X + rectangle.Width, origin.Y + rectangle.Height);
    }
    private static bool HasStyle(nint handle, long style)
    {
        nint current = NativeMethods.GetWindowLongPtrW(handle, NativeMethods.GwlpStyle);
        return (current.ToInt64() & style) == style;
    }

    private static Rectangle RectangleOf(nint handle)
    {
        if (!NativeMethods.GetWindowRect(handle, out NativeRect rect))
        {
            throw new InvalidOperationException(
                $"GetWindowRect failed for handle {handle.ToInt64()} with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private sealed class PictureProbe
    {
        internal nint ControlHandle { get; set; }

        internal nint AttachedSurface { get; set; }

        internal Rectangle PictureBounds { get; set; }

        internal Rectangle ControlRectangle { get; set; }

        internal Rectangle AfterAttach { get; set; }

        internal Rectangle AfterShow { get; set; }

        internal Rectangle AfterPump { get; set; }

        internal Rectangle AfterResize { get; set; }

        internal Rectangle AfterSettledLayout { get; set; }

        internal int CaptionHitAfterSettle { get; set; }

        internal Rectangle ResizedPictureBounds { get; set; }

        internal bool StyleHasChild { get; set; }

        internal bool StyleHasVisible { get; set; }

        internal string? Failure { get; set; }
    }
}