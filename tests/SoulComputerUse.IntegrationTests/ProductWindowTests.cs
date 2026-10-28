using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Native;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// Integration tests for the M1 product window: a real top-level window is created, real
/// <c>WM_NCHITTEST</c> messages are answered, real clicks are dispatched and the client area is really
/// painted. The visual judgement of the picture is a human acceptance step and is not asserted here.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ProductWindowTests
{
    // HTCLIENT and HTCAPTION from WinUser.h.
    private const int ClientHitCode = 1;
    private const int CaptionHitCode = 2;
    private const int WindowWidth = 900;
    private const int WindowHeight = 600;

    private static SessionStatusVisual StartingStatus => SessionStatusVisuals.Describe(
        SessionState.Starting,
        AuthTier.HelloEnrolled,
        GateStrength.Strong,
        null);

    [TestMethod]
    public void ClientAreaCoversTheWholeWindow()
    {
        using ProductWindow window = CreateWindow(() => false);

        Rectangle windowRectangle = ReadWindowRectangle(window.Handle);
        Rectangle clientRectangle = ReadClientRectangle(window.Handle);

        Assert.AreEqual(WindowWidth, windowRectangle.Width, "The window was created with the requested width.");
        Assert.AreEqual(
            windowRectangle.Width,
            clientRectangle.Width,
            "There is no non-client area left: the client rectangle covers the whole window.");
        Assert.AreEqual(
            windowRectangle.Height,
            clientRectangle.Height,
            "There is no non-client area left: the client rectangle covers the whole window.");
        Assert.AreEqual(
            WindowWidth,
            window.Metrics.ClientWidth,
            "The caption is laid out for the real client width, not for an assumed one.");
    }

    [TestMethod]
    public void HitTestAnswersSystemCodesForTheButtonsAndHtCaptionForTheBar()
    {
        using ProductWindow window = CreateWindow(() => false);
        CaptionMetrics metrics = window.Metrics;
        int middleY = metrics.Height / 2;

        Assert.AreEqual(
            20,
            window.ProbeHitTestCode(CentreX(metrics.CloseBounds), middleY),
            "Close must answer HTCLOSE so the system sends WM_CLOSE, like the taskbar and Alt+F4 do.");
        Assert.AreEqual(
            9,
            window.ProbeHitTestCode(CentreX(metrics.MaximizeBounds), middleY),
            "Maximize must answer HTMAXBUTTON so the system performs the action.");
        Assert.AreEqual(
            8,
            window.ProbeHitTestCode(CentreX(metrics.MinimizeBounds), middleY),
            "Minimize must answer HTMINBUTTON so the system performs the action.");
        Assert.AreEqual(
            CaptionHitCode,
            window.ProbeHitTestCode(200, middleY),
            "The caption strip must stay HTCAPTION so the window can be dragged and double-clicked.");
        Assert.AreEqual(
            ClientHitCode,
            window.ProbeHitTestCode(200, metrics.Height + 40),
            "The desktop picture below the caption is an ordinary client area.");
    }

    [TestMethod]
    public void CaptionButtonsUseTheScaledTokensAtTheMachineDpi()
    {
        using ProductWindow window = CreateWindow(() => false);
        CaptionMetrics metrics = window.Metrics;

        Assert.AreEqual(
            metrics.ClientWidth,
            metrics.CloseBounds.Right,
            "Close must be flush with the right edge at the machine DPI.");
        Assert.AreEqual(
            CaptionMetrics.Scale(CaptionMetrics.ButtonWidth, metrics.Dpi),
            metrics.CloseBounds.Width,
            "The button width must be the token scaled by the machine DPI.");
        Assert.AreEqual(
            metrics.Height,
            metrics.CloseBounds.Height,
            "A caption button is as tall as the caption strip.");
        Assert.AreEqual(
            metrics.MaximizeBounds.Right,
            metrics.CloseBounds.Left,
            "Maximize and close must touch: a gap there would be a drag area right next to the close button.");
    }

    [TestMethod]
    public void WindowEdgesAnswerTheResizeHitCodes()
    {
        using ProductWindow window = CreateWindow(() => false);
        CaptionMetrics metrics = window.Metrics;
        int border = CaptionMetrics.Scale(WindowFrameHitTest.ResizeBorderPixels, metrics.Dpi);
        int middleX = WindowWidth / 2;
        int middleY = WindowHeight / 2;

        Assert.AreEqual(10, window.ProbeHitTestCode(0, middleY), "The left edge must resize from the left.");
        Assert.AreEqual(11, window.ProbeHitTestCode(WindowWidth - 1, middleY), "The right edge must resize from the right.");
        Assert.AreEqual(12, window.ProbeHitTestCode(middleX, 0), "The top edge must resize from the top.");
        Assert.AreEqual(15, window.ProbeHitTestCode(middleX, WindowHeight - 1), "The bottom edge must resize from below.");
        Assert.AreEqual(13, window.ProbeHitTestCode(0, 0), "The top-left corner must resize both ways.");
        Assert.AreEqual(17, window.ProbeHitTestCode(WindowWidth - 1, WindowHeight - 1), "The bottom-right corner must resize both ways.");
        Assert.AreEqual(
            ClientHitCode,
            window.ProbeHitTestCode(border, middleY),
            "One pixel inside the resize handle is already the client area.");
    }

    [TestMethod]
    public void CaptionButtonsWinOverTheResizeHandleAtTheTopEdge()
    {
        using ProductWindow window = CreateWindow(() => false);
        CaptionMetrics metrics = window.Metrics;
        int closeX = CentreX(metrics.CloseBounds);

        Assert.AreEqual(
            20,
            window.ProbeHitTestCode(closeX, 0),
            "The very first pixel row of the close button must stay a button, not a resize handle.");
        Assert.AreEqual(CaptionPart.Close, window.ProbeCaptionPart(closeX, 0));
    }

    [TestMethod]
    public void MaximizedWindowFitsTheMonitorWorkAreaSoTheTaskbarStaysVisible()
    {
        using ProductWindow window = CreateWindow(() => false);

        window.SendSystemMaximizeCommand();
        Assert.IsTrue(window.IsMaximized);

        Rectangle workArea = ReadMonitorWorkArea(window.Handle);
        Rectangle clientInScreen = ReadClientRectangleInScreen(window.Handle);
        Rectangle monitorRectangle = ReadMonitorRectangle(window.Handle);

        Assert.AreEqual(
            workArea,
            clientInScreen,
            "The client area of a maximized window must be the monitor work area, so the taskbar stays visible.");
        Assert.AreNotEqual(
            monitorRectangle,
            workArea,
            "On this machine the work area must differ from the monitor, otherwise the check would pass even with a window that covers the taskbar.");
        Assert.AreEqual(
            clientInScreen.Width,
            window.Metrics.ClientWidth,
            "The caption must be laid out for the maximized client width.");
    }

    [TestMethod]
    public void MaximizeButtonDrivesTheSystemMaximizeCommand()
    {
        using ProductWindow window = CreateWindow(() => false);
        int clickX = CentreX(window.Metrics.MaximizeBounds);
        int clickY = window.Metrics.Height / 2;

        Assert.AreEqual(CaptionPart.Maximize, window.ProbeCaptionPart(clickX, clickY));

        window.SendSystemMaximizeCommand();
        Assert.IsTrue(window.IsMaximized, "The system must maximize the window when the button answers HTMAXBUTTON.");

        window.SendSystemRestoreCommand();
        Assert.IsFalse(window.IsMaximized, "A second system maximize command must restore the window.");
    }

    [TestMethod]
    public void MaximizedWindowHasNoResizeHandles()
    {
        using ProductWindow window = CreateWindow(() => false);
        window.SendSystemMaximizeCommand();

        Assert.AreEqual(
            ClientHitCode,
            window.ProbeHitTestCode(0, WindowHeight / 2),
            "A maximized window cannot be resized, so its left edge is no longer a handle.");
        Assert.AreEqual(
            ClientHitCode,
            window.ProbeHitTestCode(WindowWidth - 1, WindowHeight - 1),
            "A maximized window cannot be resized, so its bottom-right corner is no longer a handle.");
    }

    [TestMethod]
    public void SystemCloseCommandIsTheOnlyWayInAndItAsksFirst()
    {
        int confirmations = 0;
        using ProductWindow window = CreateWindow(() =>
        {
            confirmations++;
            return false;
        });
        SessionStatusVisual statusBefore = window.Status;

        // The title-bar close button answers HTCLOSE, so the system sends SC_CLOSE; that command must
        // land in WM_CLOSE and ask the operator, and nothing may change before the answer.
        window.SendSystemCloseCommand();

        Assert.AreEqual(1, confirmations, "The system close command must reach the confirmation step.");
        Assert.IsFalse(window.IsDestroyed, "A refused confirmation must leave the window open.");
        Assert.AreEqual(
            statusBefore,
            window.Status,
            "The session status must stay unchanged until the operator confirms.");
    }

    [TestMethod]
    public void SystemCloseCommandDestroysTheWindowAfterConfirmation()
    {
        int confirmations = 0;
        using ProductWindow window = CreateWindow(() =>
        {
            confirmations++;
            return true;
        });

        window.SendSystemCloseCommand();

        Assert.AreEqual(1, confirmations, "The system close command must ask exactly once.");
        Assert.IsTrue(window.IsDestroyed, "A confirmed close must destroy the window.");
    }

    [TestMethod]
    public void TheSessionPictureIsLaidOutInsideTheClientAreaAndTheRestStaysLetterbox()
    {
        using ProductWindow window = CreateWindow(() => false);

        Assert.AreEqual(
            Rectangle.Empty,
            window.PictureBounds,
            "Before the session reports a picture size there is no picture rectangle to draw into.");

        window.SetSessionSize(1920, 1080);

        Rectangle picture = window.PictureBounds;
        Rectangle client = ReadClientRectangle(window.Handle);

        Assert.IsTrue(picture.Width > 0 && picture.Height > 0, "The picture must have a real size once the session reports one.");
        Assert.IsTrue(
            picture.Left >= 0 && picture.Top >= 0 && picture.Right <= client.Right && picture.Bottom <= client.Bottom,
            "The picture rectangle must stay inside the client area; everything outside it is the black letterbox.");
        Assert.AreEqual(
            picture.Left,
            client.Right - picture.Right,
            "A picture fitted into a different aspect ratio must be centred, so both bars match.");
    }

    [TestMethod]
    public void AttachingAPictureSurfacePlacesItOnThePictureRectangle()
    {
        using ProductWindow window = CreateWindow(() => false);
        using ProductWindow surface = CreateWindow(() => false);

        window.SetSessionSize(1920, 1080);
        window.AttachPictureSurface(surface.Handle, (parent, bounds) => PlaceOnRectangle(parent, surface.Handle, bounds));

        Assert.AreEqual(surface.Handle, window.PictureSurface);
        Assert.AreEqual(
            window.PictureBounds,
            ReadWindowRectangleInClientCoordinates(surface.Handle),
            "The surface covers exactly the picture rectangle and never paints over the letterbox.");

        window.SetSessionSize(1024, 768);
        Assert.AreEqual(
            window.PictureBounds,
            ReadWindowRectangleInClientCoordinates(surface.Handle),
            "A new session size must move and resize the surface with the picture rectangle.");

        window.DetachPictureSurface();
        Assert.AreEqual(nint.Zero, window.PictureSurface);
    }

    private static void PlaceOnRectangle(nint parent, nint handle, Rectangle bounds)
    {
        nint style = NativeMethods.GetWindowLongPtrW(handle, NativeMethods.GwlpStyle);
        _ = NativeMethods.SetWindowLongPtrW(handle, NativeMethods.GwlpStyle, style | (nint)NativeMethods.WsChild);
        _ = NativeMethods.SetParent(handle, parent);
        bool placed = NativeMethods.SetWindowPos(
            handle,
            0,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        Assert.IsTrue(placed, "The test surface must accept the rectangle it is placed on.");
    }

    [TestMethod]
    public void AWindowTooNarrowOrTallForTheSessionStillKeepsThePictureInside()
    {
        using ProductWindow window = CreateWindow(() => false);

        window.SetSessionSize(1, 1);
        Rectangle squarePicture = window.PictureBounds;
        Rectangle client = ReadClientRectangle(window.Handle);

        Assert.IsTrue(
            squarePicture.Left >= 0 && squarePicture.Right <= client.Right && squarePicture.Bottom <= client.Bottom,
            "Even a square session picture must stay inside a wide client area.");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => window.SetSessionSize(0, 1080));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => window.SetSessionSize(1920, 0));
    }

    [TestMethod]
    public void CloseAsksTheOperatorAndStaysOpenWhenTheAnswerIsNo()
    {
        int confirmations = 0;
        using ProductWindow window = CreateWindow(() =>
        {
            confirmations++;
            return false;
        });

        window.SendCloseRequest();

        Assert.AreEqual(1, confirmations, "Closing must ask before ending the session.");
        Assert.IsFalse(window.IsDestroyed, "A refused confirmation must leave the window open.");
    }

    [TestMethod]
    public void CloseDestroysTheWindowWhenTheOperatorConfirms()
    {
        int confirmations = 0;
        using ProductWindow window = CreateWindow(() =>
        {
            confirmations++;
            return true;
        });

        window.SendCloseRequest();

        Assert.AreEqual(1, confirmations, "Closing must ask exactly once.");
        Assert.IsTrue(window.IsDestroyed, "A confirmed close must destroy the window.");
    }

    [TestMethod]
    public void EverySessionStatePaintsWithoutThrowing()
    {
        using ProductWindow window = CreateWindow(() => false);

        foreach (SessionState state in Enum.GetValues<SessionState>())
        {
            if (state == SessionState.Blocked)
            {
                foreach (BlockedCause cause in Enum.GetValues<BlockedCause>())
                {
                    window.SetStatus(SessionStatusVisuals.Describe(
                        SessionState.Blocked,
                        AuthTier.HelloEnrolled,
                        GateStrength.Strong,
                        cause));
                    window.RepaintNow();
                }

                continue;
            }

            window.SetStatus(SessionStatusVisuals.Describe(
                state,
                AuthTier.HelloEnrolled,
                GateStrength.Strong,
                null));
            window.RepaintNow();
        }

        window.SetStatus(SessionStatusVisuals.Describe(
            SessionState.Ready,
            AuthTier.SystemCredential,
            GateStrength.Login,
            null));
        window.RepaintNow();
    }

    [TestMethod]
    public void AStatusPostedFromAWorkerThreadIsAppliedOnTheWindowThread()
    {
        using ProductWindow window = CreateWindow(() => false);
        SessionStatusVisual disconnected = SessionStatusVisuals.Describe(
            SessionState.Disconnected,
            AuthTier.HelloEnrolled,
            GateStrength.Strong,
            null);

        Task.Run(() => window.PostStatus(disconnected)).GetAwaiter().GetResult();

        for (int attempt = 0; attempt < 100 && window.Status != disconnected; attempt++)
        {
            _ = MessagePump.PumpOnce();
            Thread.Sleep(10);
        }

        Assert.AreEqual(disconnected, window.Status);
    }

    [TestMethod]
    public void TitleChangeReachesTheCaptionAndTheTaskbar()
    {
        using ProductWindow window = CreateWindow(() => false);

        window.SetTitle("SoulComputerUse — example-session");

        Assert.AreEqual("SoulComputerUse — example-session", window.Title);
    }

    [TestMethod]
    public void DisposeIsIdempotentAndRefusesFurtherUse()
    {
        ProductWindow window = CreateWindow(() => false);
        window.Dispose();
        window.Dispose();

        Assert.IsTrue(window.IsDestroyed);
        Assert.ThrowsExactly<ObjectDisposedException>(() => window.ProbeHitTestCode(10, 10));
    }

    private static ProductWindow CreateWindow(Func<bool> confirmClose)
    {
        return ProductWindow.Create(
            WindowWidth,
            WindowHeight,
            "SoulComputerUse — example-session",
            confirmClose,
            StartingStatus);
    }

    /// <summary>
    /// A real non-client press and release on the maximize button must actually maximize the window,
    /// then restore it on the second click. The prior implementation only painted the pressed look:
    /// with no non-client area the default procedure never completed the press cycle.
    /// </summary>
    [TestMethod]
    public void ClickingTheMaximizeButtonReallyMaximizesAndRestores()
    {
        using ProductWindow window = CreateWindow(() => false);
        int clickX = CentreX(window.Metrics.MaximizeBounds);
        int clickY = window.Metrics.Height / 2;

        PressAndRelease(window, NativeMethods.HtMaxButton, clickX, clickY);
        Assert.IsTrue(window.IsMaximized, "A real click on the maximize button must maximize the window.");

        clickX = CentreX(window.Metrics.MaximizeBounds);
        clickY = window.Metrics.Height / 2;
        PressAndRelease(window, NativeMethods.HtMaxButton, clickX, clickY);
        Assert.IsFalse(window.IsMaximized, "A second real click must restore the window.");
    }

    /// <summary>The close button must still arrive at the confirmation step, never close directly.</summary>
    [TestMethod]
    public void ClickingTheCloseButtonAsksBeforeClosing()
    {
        int confirmations = 0;
        using ProductWindow window = CreateWindow(() =>
        {
            confirmations++;
            return false;
        });

        PressAndRelease(
            window,
            NativeMethods.HtClose,
            CentreX(window.Metrics.CloseBounds),
            window.Metrics.Height / 2);

        Assert.AreEqual(1, confirmations, "A real click on close must reach the confirmation exactly once.");
        Assert.IsFalse(window.IsDestroyed, "A refused confirmation must leave the window open.");
    }

    /// <summary>
    /// The window keeps the styles the window manager and Snap Layouts need: a maximize box and a
    /// resize frame. Removing either would silently break Snap Layouts and resizing.
    /// </summary>
    [TestMethod]
    public void TheWindowKeepsTheMaximizeBoxAndResizeFrameStyles()
    {
        using ProductWindow window = CreateWindow(() => false);
        long style = (long)NativeMethods.GetWindowLongPtrW(window.Handle, NativeMethods.GwlpStyle);

        Assert.AreNotEqual(
            0,
            style & NativeMethods.WsMaximizeBox,
            "Without WS_MAXIMIZEBOX the maximize button and Snap Layouts cannot work.");
        Assert.AreNotEqual(
            0,
            style & NativeMethods.WsThickFrame,
            "Without WS_THICKFRAME the window cannot be resized or snapped.");
    }

    /// <summary>
    /// A release can arrive as a client message once the press captured the pointer. That path must
    /// complete the action too, otherwise a real mouse click would still do nothing.
    /// </summary>
    [TestMethod]
    public void AClickWhoseReleaseArrivesAsAClientMessageStillMaximizes()
    {
        using ProductWindow window = CreateWindow(() => false);
        int clickX = CentreX(window.Metrics.MaximizeBounds);
        int clickY = window.Metrics.Height / 2;

        PressNonClient(window, NativeMethods.HtMaxButton, clickX, clickY);
        _ = NativeMethods.SendMessageW(
            window.Handle,
            NativeMethods.WmLButtonUp,
            0,
            NativeMethods.PackWords(clickX, clickY));

        Assert.IsTrue(
            window.IsMaximized,
            "A release delivered as WM_LBUTTONUP after capture must still perform the button action.");
    }

    /// <summary>Dragging off the button and releasing there must not run the action.</summary>
    [TestMethod]
    public void ReleasingAwayFromTheButtonDoesNotRunTheAction()
    {
        using ProductWindow window = CreateWindow(() => false);
        PressNonClient(
            window,
            NativeMethods.HtMaxButton,
            CentreX(window.Metrics.MaximizeBounds),
            window.Metrics.Height / 2);

        _ = NativeMethods.SendMessageW(
            window.Handle,
            NativeMethods.WmLButtonUp,
            0,
            NativeMethods.PackWords(200, window.Metrics.Height / 2));

        Assert.IsFalse(window.IsMaximized, "Releasing over the caption strip must cancel the maximize click.");
    }

    private static void PressAndRelease(ProductWindow window, int hitTestCode, int clientX, int clientY)
    {
        PressNonClient(window, hitTestCode, clientX, clientY);

        Rectangle windowRectangle = ReadWindowRectangle(window.Handle);
        nint packedPoint = NativeMethods.PackWords(windowRectangle.Left + clientX, windowRectangle.Top + clientY);
        _ = NativeMethods.SendMessageW(window.Handle, NativeMethods.WmNcLButtonUp, hitTestCode, packedPoint);
    }

    private static void PressNonClient(ProductWindow window, int hitTestCode, int clientX, int clientY)
    {
        Rectangle windowRectangle = ReadWindowRectangle(window.Handle);
        nint packedPoint = NativeMethods.PackWords(windowRectangle.Left + clientX, windowRectangle.Top + clientY);
        _ = NativeMethods.SendMessageW(window.Handle, NativeMethods.WmNcLButtonDown, hitTestCode, packedPoint);
    }

    private static int CentreX(Rectangle bounds)
    {
        return bounds.Left + (bounds.Width / 2);
    }

    private static Rectangle ReadWindowRectangle(nint handle)
    {
        NativeRect windowRectangle = ReadRect(handle, useWindowRectangle: true);
        return Rectangle.FromLTRB(
            windowRectangle.Left,
            windowRectangle.Top,
            windowRectangle.Right,
            windowRectangle.Bottom);
    }

    private static Rectangle ReadClientRectangle(nint handle)
    {
        NativeRect clientRectangle = ReadRect(handle, useWindowRectangle: false);
        return Rectangle.FromLTRB(
            clientRectangle.Left,
            clientRectangle.Top,
            clientRectangle.Right,
            clientRectangle.Bottom);
    }

    private static Rectangle ReadMonitorRectangle(nint handle)
    {
        nint monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MonitorDefaultToNearest);
        Assert.AreNotEqual(nint.Zero, monitor, "The window must belong to a monitor.");

        MonitorInfo monitorInfo = new()
        {
            Size = Marshal.SizeOf<MonitorInfo>(),
        };
        Assert.IsTrue(NativeMethods.GetMonitorInfo(monitor, ref monitorInfo), "Reading the monitor rectangle must succeed.");

        return Rectangle.FromLTRB(
            monitorInfo.Monitor.Left,
            monitorInfo.Monitor.Top,
            monitorInfo.Monitor.Right,
            monitorInfo.Monitor.Bottom);
    }

    /// <summary>Reads a child window's rectangle relative to its parent's client origin.</summary>
    private static Rectangle ReadWindowRectangleInClientCoordinates(nint handle)
    {
        Rectangle windowRectangle = ReadWindowRectangle(handle);
        NativePoint origin = new()
        {
            X = windowRectangle.Left,
            Y = windowRectangle.Top,
        };
        nint parent = NativeMethods.GetParent(handle);
        Assert.AreNotEqual(nint.Zero, parent, "The surface must be parented to the product window.");
        Assert.IsTrue(NativeMethods.ScreenToClient(parent, ref origin), "Converting the surface origin must succeed.");

        return Rectangle.FromLTRB(
            origin.X,
            origin.Y,
            origin.X + windowRectangle.Width,
            origin.Y + windowRectangle.Height);
    }

    private static Rectangle ReadClientRectangleInScreen(nint handle)    {
        NativeRect client = ReadRect(handle, useWindowRectangle: false);
        NativePoint origin = new()
        {
            X = 0,
            Y = 0,
        };
        Assert.IsTrue(NativeMethods.ClientToScreen(handle, ref origin), "Reading the client origin must succeed.");
        return Rectangle.FromLTRB(
            origin.X,
            origin.Y,
            origin.X + (client.Right - client.Left),
            origin.Y + (client.Bottom - client.Top));
    }

    private static Rectangle ReadMonitorWorkArea(nint handle)
    {
        nint monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MonitorDefaultToNearest);
        Assert.AreNotEqual(nint.Zero, monitor, "The window must belong to a monitor.");

        MonitorInfo monitorInfo = new()
        {
            Size = Marshal.SizeOf<MonitorInfo>(),
        };
        Assert.IsTrue(NativeMethods.GetMonitorInfo(monitor, ref monitorInfo), "Reading the monitor work area must succeed.");

        return Rectangle.FromLTRB(
            monitorInfo.Work.Left,
            monitorInfo.Work.Top,
            monitorInfo.Work.Right,
            monitorInfo.Work.Bottom);
    }

    private static NativeRect ReadRect(nint handle, bool useWindowRectangle)
    {
        NativeRect rect;
        bool read = useWindowRectangle
            ? NativeMethods.GetWindowRect(handle, out rect)
            : NativeMethods.GetClientRect(handle, out rect);
        Assert.IsTrue(read, "Reading the rectangle must succeed for a live window.");
        return rect;
    }
}
