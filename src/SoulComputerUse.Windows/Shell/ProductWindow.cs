using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using SoulComputerUse.Windows.Native;

namespace SoulComputerUse.Windows.Shell;

/// <summary>
/// The product window: one real top-level window whose client area covers the whole frame, with the
/// caption strip drawn by <see cref="CaptionPainter"/> at the top and the letterboxed desktop picture
/// below it.
/// </summary>
/// <remarks>
/// <para>
/// <c>WM_NCCALCSIZE</c> answers zero, so there is no non-client area left to draw into; the caption
/// you see is painted inside the client rectangle. The window keeps the standard overlapped style, so
/// Alt+F4, the taskbar menu, the system menu and the Win+Arrow snap shortcuts keep working.
/// </para>
/// <para>
/// The caption strip answers <c>HTCAPTION</c> and the three buttons answer their system codes
/// (<c>HTMINBUTTON</c>, <c>HTMAXBUTTON</c>, <c>HTCLOSE</c>), so the window manager still offers Snap
/// Layouts on the maximize button and keeps every native close route. Because the window has no
/// non-client area, the default procedure cannot complete a non-client button press, so the press
/// cycle and the action are run here; a close still goes through <c>WM_CLOSE</c> and therefore
/// through the confirmation.
/// </para>
/// </remarks>
public sealed class ProductWindow : IDisposable
{
    /// <summary>The registered window class name.</summary>
    public const string WindowClassName = "SoulComputerUse.ProductWindow";

    /// <summary>A private message used to apply a status posted from a worker thread.</summary>
    private const uint WmAppStatus = 0x8000 + 1;

    private static readonly object RegistrationLock = new();
    private static readonly Dictionary<nint, ProductWindow> OpenWindows = new();
    private static readonly WindowProcedure WindowDelegate = HandleWindowMessage;

    private static ushort _classAtom;

    private readonly Func<bool> _confirmClose;

    private readonly ConcurrentQueue<SessionStatusVisual> _postedStatuses = new();
    private nint _handle;
    private string _title;
    private SessionStatusVisual _status;
    private CaptionMetrics _metrics;
    private CaptionButtonKind? _hoveredButton;
    private CaptionButtonKind? _pressedButton;
    private nint _pictureSurface;
    private int _sessionWidth;
    private int _sessionHeight;
    private bool _isActive;
    private bool _trackingMouseLeave;
    private bool _destroyed;
    private bool _disposed;

    private ProductWindow(nint handle, string title, Func<bool> confirmClose, SessionStatusVisual status)
    {
        _handle = handle;
        _title = title;
        _confirmClose = confirmClose;
        _status = status;
        int dpi = DpiProbe.GetWindowDpi(handle);
        int textScalePercent = TextScaleProbe.GetTextScalePercent();
        _metrics = CaptionMetrics.Create(ReadClientWidth(handle), dpi, textScalePercent);
        _ = NativeMethods.SendMessageW(handle, NativeMethods.WmSetIcon, NativeMethods.IconBig, BrandIcon.Handle);
        _ = NativeMethods.SendMessageW(handle, NativeMethods.WmSetIcon, NativeMethods.IconSmall, BrandIcon.Handle);
    }

    /// <summary>The window handle, or zero once the window was destroyed.</summary>
    public nint Handle => _handle;

    /// <summary>True once the window received <c>WM_DESTROY</c>.</summary>
    public bool IsDestroyed => _destroyed;

    /// <summary>True while the window is maximized.</summary>
    public bool IsMaximized
    {
        get
        {
            nint handle = RequireHandle();
            return NativeMethods.IsZoomed(handle);
        }
    }

    /// <summary>The measured caption layout for the current client width and DPI.</summary>
    public CaptionMetrics Metrics => _metrics;

    /// <summary>The status the caption currently shows.</summary>
    public SessionStatusVisual Status => _status;

    /// <summary>
    /// Where the desktop picture is drawn inside the client area. Empty until the session reports its
    /// picture size; the rest of the client area is the letterbox and stays pure black.
    /// </summary>
    public Rectangle PictureBounds { get; private set; }

    /// <summary>The child window that renders the desktop picture, or zero when none is attached.</summary>
    public nint PictureSurface => _pictureSurface;

    /// <summary>
    /// Reports the session picture size, which fixes the letterbox, and repositions the picture
    /// surface if one is attached.
    /// </summary>
    public void SetSessionSize(int sessionWidth, int sessionHeight)
    {
        if (sessionWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionWidth), sessionWidth, "The session width must be positive.");
        }

        if (sessionHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionHeight), sessionHeight, "The session height must be positive.");
        }

        _sessionWidth = sessionWidth;
        _sessionHeight = sessionHeight;
        RecomputePictureBounds();
        RepositionPictureSurface();
        RequestRepaint();
    }

    /// <summary>
    /// Adopts the window that renders the desktop picture and keeps it exactly on
    /// <see cref="PictureBounds"/>, so the letterbox is never painted over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The surface owns its own window tree, so it parents and positions itself through
    /// <paramref name="adopt"/>; this window only decides the rectangle. That split matters because the
    /// remote desktop picture is a hosted ActiveX control inside a Windows Forms carrier: only the surface
    /// knows that the window to adopt is the carrier and that the control must stay docked inside it.
    /// Reparenting a bare ActiveX control window from here would leave the control outside the layer that
    /// owns its layout, which is how the picture came to cover the caption strip.
    /// </para>
    /// <para>
    /// The callback runs again whenever <see cref="SetSessionSize"/> or the client area changes, so the
    /// picture follows the window instead of being placed once.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">The adoption callback is null.</exception>
    /// <exception cref="ArgumentException">The surface handle is zero.</exception>
    public void AttachPictureSurface(nint surface, Action<nint, Rectangle> adopt)
    {
        ArgumentNullException.ThrowIfNull(adopt);
        if (surface == 0)
        {
            throw new ArgumentException("The picture surface handle must not be zero.", nameof(surface));
        }

        nint handle = RequireHandle();
        _pictureSurface = surface;
        _adoptPicture = adopt;
        adopt(handle, PictureBounds);
    }

    private Action<nint, Rectangle>? _adoptPicture;

    /// <summary>Detaches the picture surface without destroying it.</summary>
    public void DetachPictureSurface()
    {
        if (_pictureSurface != 0)
        {
            _ = NativeMethods.SetParent(_pictureSurface, 0);
        }

        _pictureSurface = 0;
        _adoptPicture = null;
    }

    /// <summary>The title the caption currently shows.</summary>
    public string Title => _title;

    /// <summary>
    /// Creates and shows the window. The client area is exactly <paramref name="clientWidth"/> by
    /// <paramref name="clientHeight"/> physical pixels, caption strip included.
    /// </summary>
    /// <param name="clientWidth">Client width in physical pixels, at least <see cref="CaptionMetrics.MinimumClientWidth"/> at the current DPI.</param>
    /// <param name="clientHeight">Client height in physical pixels.</param>
    /// <param name="title">The window title, also shown by the taskbar.</param>
    /// <param name="confirmClose">Asks the operator whether the session may end; false cancels the close.</param>
    /// <param name="initialStatus">The status the caption shows until the session reports another one.</param>
    public static ProductWindow Create(
        int clientWidth,
        int clientHeight,
        string title,
        Func<bool> confirmClose,
        SessionStatusVisual initialStatus)
    {
        if (clientWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clientWidth), clientWidth, "The client width must be positive.");
        }

        if (clientHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clientHeight), clientHeight, "The client height must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(confirmClose);
        ArgumentNullException.ThrowIfNull(initialStatus);

        lock (RegistrationLock)
        {
            ushort classAtom = EnsureClassRegistered();
            nint handle = NativeMethods.CreateWindowExW(
                0,
                WindowClassName,
                title,
                NativeMethods.WsOverlappedWindow | NativeMethods.WsVisible,
                NativeMethods.CwUseDefault,
                NativeMethods.CwUseDefault,
                clientWidth,
                clientHeight,
                0,
                0,
                NativeMethods.GetModuleHandleW(null),
                0);
            if (handle == 0)
            {
                throw NativeMethods.InteropFailure(
                    $"CreateWindowExW(class={WindowClassName}, atom={classAtom}, w={clientWidth}, h={clientHeight})",
                    Marshal.GetLastWin32Error());
            }

            try
            {
                ApplyBorderlessFrame(handle);
                ProductWindow window = new(handle, title, confirmClose, initialStatus);
                OpenWindows[handle] = window;
                window.RepaintNow();
                return window;
            }
            catch (ArgumentException)
            {
                // The window exists but its caption cannot be laid out, for example because the DPI
                // scaled minimum width is larger than the requested width. Never leak a half-built
                // window, and never swallow the reason.
                _ = NativeMethods.DestroyWindow(handle);
                throw;
            }
        }
    }

    /// <summary>Replaces the status the caption shows and repaints.</summary>
    public void SetStatus(SessionStatusVisual status)
    {
        ArgumentNullException.ThrowIfNull(status);
        _status = status;
        RequestRepaint();
    }

    /// <summary>
    /// Queues a status update from a worker thread. The window procedure applies it on the thread that
    /// owns the window, so the owner pipe can report a blocked or disconnected session without touching
    /// window state across threads.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="status"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The window was destroyed.</exception>
    /// <exception cref="InvalidOperationException">The message could not be posted.</exception>
    public void PostStatus(SessionStatusVisual status)
    {
        ArgumentNullException.ThrowIfNull(status);
        nint handle = RequireHandle();
        _postedStatuses.Enqueue(status);
        if (!NativeMethods.PostMessageW(handle, WmAppStatus, 0, 0))
        {
            throw new InvalidOperationException(
                "The status update could not be posted to the product window.");
        }
    }

    private void ApplyPostedStatus()
    {
        while (_postedStatuses.TryDequeue(out SessionStatusVisual? status))
        {
            if (status is not null)
            {
                _status = status;
            }
        }

        RequestRepaint();
    }

    /// <summary>Replaces the window title and repaints.</summary>
    public void SetTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        _title = title;
        nint handle = RequireHandle();
        if (!NativeMethods.SetWindowTextW(handle, title))
        {
            throw NativeMethods.InteropFailure("SetWindowTextW", Marshal.GetLastWin32Error());
        }

        RequestRepaint();
    }

    /// <summary>Sends a real <c>WM_NCHITTEST</c> for a client-area point and returns the hit code.</summary>
    public int ProbeHitTestCode(int clientX, int clientY)
    {
        nint handle = RequireHandle();
        Rectangle windowRectangle = ReadWindowRectangle(handle);
        return (int)NativeMethods.SendMessageW(
            handle,
            NativeMethods.WmNcHitTest,
            0,
            NativeMethods.PackWords(windowRectangle.Left + clientX, windowRectangle.Top + clientY));
    }

    /// <summary>Resolves the caption part under a client-area point, without sending a message.</summary>
    public CaptionPart ProbeCaptionPart(int clientX, int clientY)
    {
        return CaptionHitTest.Resolve(_metrics, clientX, clientY);
    }

    /// <summary>
    /// Sends the system command a maximize or restore click produces, so the maximize path can be
    /// exercised without a physical mouse press.
    /// </summary>
    public void SendSystemMaximizeCommand()
    {
        nint handle = RequireHandle();
        NativeMethods.SendMessageW(handle, NativeMethods.WmSysCommand, NativeMethods.ScMaximize, 0);
    }

    /// <summary>Sends the system command a restore click produces.</summary>
    public void SendSystemRestoreCommand()
    {
        nint handle = RequireHandle();
        NativeMethods.SendMessageW(handle, NativeMethods.WmSysCommand, NativeMethods.ScRestore, 0);
    }

    /// <summary>
    /// Sends the system close command, which is the path the title-bar close button and the taskbar
    /// close item take; it must end in <c>WM_CLOSE</c> and therefore in the confirmation step.
    /// </summary>
    public void SendSystemCloseCommand()
    {
        nint handle = RequireHandle();
        NativeMethods.SendMessageW(handle, NativeMethods.WmSysCommand, NativeMethods.ScClose, 0);
    }

    /// <summary>Forces a synchronous repaint of the whole client area.</summary>
    public void RepaintNow()
    {
        nint handle = RequireHandle();
        if (!NativeMethods.InvalidateRect(handle, 0, false))
        {
            throw NativeMethods.InteropFailure("InvalidateRect", Marshal.GetLastWin32Error());
        }

        if (!NativeMethods.UpdateWindow(handle))
        {
            throw NativeMethods.InteropFailure("UpdateWindow", Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Sends a real <c>WM_CLOSE</c>, which runs the confirmation callback.</summary>
    public void SendCloseRequest()
    {
        nint handle = RequireHandle();
        NativeMethods.SendMessageW(handle, NativeMethods.WmClose, 0, 0);
    }

    /// <summary>Destroys the window if it still exists.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        nint handle = _handle;
        if (handle != 0 && !_destroyed)
        {
            _ = NativeMethods.DestroyWindow(handle);
        }

        _handle = 0;
        lock (RegistrationLock)
        {
            OpenWindows.Remove(handle);
        }
    }

    private static nint HandleWindowMessage(nint handle, uint message, nint wParam, nint lParam)
    {
        OpenWindows.TryGetValue(handle, out ProductWindow? window);
        switch (message)
        {
            case NativeMethods.WmNcCalcSize when wParam != 0:
                // Zero means "no non-client area": the client rectangle covers the whole window, so
                // the caption strip is ours to paint and there is no frame for DWM to draw into.
                // This is answered for every window of the class, because the very first
                // WM_NCCALCSIZE arrives during CreateWindowExW, before the instance is registered.
                ClampMaximizedClientToWorkArea(handle, lParam);
                return 0;
            case NativeMethods.WmNcHitTest when window is not null:
                return window.ResolveHitTest(handle, lParam);
            case NativeMethods.WmGetMinMaxInfo:
                // Answered for every window of this class: the message also arrives while the window
                // is still being created, and a maximized window that covers the taskbar is exactly
                // what this handler exists to prevent.
                ApplyMinMaxInfo(handle, lParam);
                return 0;
            case NativeMethods.WmPaint when window is not null:
                window.Paint(handle);
                return 0;
            case NativeMethods.WmEraseBkgnd when window is not null:
                return 1;
            case NativeMethods.WmNcMouseMove when window is not null:
                // A caption button answers a non-client hit code, so the pointer arrives as a
                // non-client message; the visual feedback is drawn by this window and the default
                // procedure keeps the pointer tracking that the window manager needs.
                window.UpdateHoverFromHitCode((int)wParam, handle);
                return NativeMethods.DefWindowProcW(handle, message, wParam, lParam);
            case NativeMethods.WmNcLButtonDown when window is not null:
                return window.PressCaptionButton(handle, (int)wParam, lParam);
            case NativeMethods.WmNcLButtonUp when window is not null:
                return window.ReleaseCaptionButton(handle, (int)wParam, lParam);
            case NativeMethods.WmCaptureChanged when window is not null:
                // A lost capture means the button up will never arrive, so the pressed look must
                // not be left behind and the action must not fire later by accident.
                window.CancelPressedButton();
                return 0;
            case NativeMethods.WmLButtonUp when window is not null:
                // With no non-client area the release can arrive as a client message; dropping it
                // would leave the pressed button stuck forever.
                return window.ReleaseCaptionButtonOnClientUp(handle, lParam);
            case NativeMethods.WmNcMouseLeave when window is not null:
                window.ClearPointerState();
                return 0;
            case NativeMethods.WmActivate when window is not null:
                window.UpdateActivation(wParam != 0);
                return 0;
            case NativeMethods.WmNcActivate when window is not null:
                window.UpdateActivation(wParam != 0);
                return 1;
            case NativeMethods.WmSize when window is not null:
                window.RecomputeMetrics(handle);
                return 0;
            case NativeMethods.WmDpiChanged when window is not null:
                window.ApplyDpiChange(handle, wParam, lParam);
                return 0;
            case WmAppStatus when window is not null:
                window.ApplyPostedStatus();
                return 0;
            case NativeMethods.WmClose when window is not null:
                window.RequestClose(handle);
                return 0;
            case NativeMethods.WmDestroy:
                {
                    if (window is not null)
                    {
                        window._destroyed = true;
                    }

                    OpenWindows.Remove(handle);
                }

                NativeMethods.PostQuitMessage(0);
                return 0;
            default:
                return NativeMethods.DefWindowProcW(handle, message, wParam, lParam);
        }
    }

    /// <summary>
    /// A window procedure must never throw, because the exception would unwind through the OS
    /// message dispatch. The one fallible conversion therefore reports "not our frame", which leaves
    /// the point to the default handler.
    /// </summary>
    private nint ResolveHitTest(nint handle, nint packedPoint)
    {
        if (!NativeMethods.GetWindowRect(handle, out NativeRect windowRectangle)
            || !NativeMethods.GetClientRect(handle, out NativeRect clientRectangle))
        {
            return NativeMethods.DefWindowProcW(handle, NativeMethods.WmNcHitTest, 0, packedPoint);
        }

        int clientX = NativeMethods.SignedLowWord(packedPoint) - windowRectangle.Left;
        int clientY = NativeMethods.SignedHighWord(packedPoint) - windowRectangle.Top;
        CaptionPart part = CaptionHitTest.Resolve(_metrics, clientX, clientY);
        if (CaptionHitTest.IsButton(part))
        {
            // The buttons win over everything else, so a resize handle can never swallow a click.
            return CaptionHitTest.ToHitTestCode(part);
        }

        // A maximized window cannot be resized, so its edges and corners stop being handles.
        ResizeEdge edge = NativeMethods.IsZoomed(handle)
            ? ResizeEdge.None
            : WindowFrameHitTest.ResolveResizeEdge(
                clientRectangle.Right - clientRectangle.Left,
                clientRectangle.Bottom - clientRectangle.Top,
                CaptionMetrics.Scale(WindowFrameHitTest.ResizeBorderPixels, _metrics.Dpi),
                clientX,
                clientY);
        if (edge != ResizeEdge.None)
        {
            return WindowFrameHitTest.ToHitTestCode(edge);
        }

        return CaptionHitTest.ToHitTestCode(part);
    }

    /// <summary>
    /// Recalculates the non-client area once the window exists. The first <c>WM_NCCALCSIZE</c> of a
    /// brand new window is answered before the window procedure can see a caption, so the frame it
    /// computes is still there; asking for a frame change makes the procedure drop it, and the client
    /// rectangle then covers exactly the requested number of pixels.
    /// </summary>
    private static void ApplyBorderlessFrame(nint handle)
    {
        bool applied = NativeMethods.SetWindowPos(
            handle,
            0,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged);
        if (!applied)
        {
            throw NativeMethods.InteropFailure("SetWindowPos(SWP_FRAMECHANGED)", Marshal.GetLastWin32Error());
        }
    }

    /// <summary>
    /// Keeps a maximized window inside the monitor work area. The window manager inflates every
    /// maximized window by the frame thickness so the frame falls outside the screen; this window has
    /// no frame, so the client rectangle is pinned to the work area instead and the taskbar stays
    /// visible.
    /// </summary>
    private static void ClampMaximizedClientToWorkArea(nint handle, nint lParam)
    {
        if (lParam == 0 || !NativeMethods.IsZoomed(handle))
        {
            return;
        }

        nint monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MonitorDefaultToNearest);
        if (monitor == 0)
        {
            return;
        }

        MonitorInfo monitorInfo = new()
        {
            Size = Marshal.SizeOf<MonitorInfo>(),
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        NcCalcSizeParams parameters = Marshal.PtrToStructure<NcCalcSizeParams>(lParam);
        parameters.Proposed = monitorInfo.Work;
        Marshal.StructureToPtr(parameters, lParam, false);
    }

    private static void ApplyMinMaxInfo(nint handle, nint lParam)
    {
        if (lParam == 0)
        {
            return;
        }

        MinMaxInfo info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        int dpi = DpiProbe.GetWindowDpi(handle);
        info.MinTrackSize = new NativePoint
        {
            X = CaptionMetrics.Scale(CaptionMetrics.MinimumClientWidth, dpi),
            Y = CaptionMetrics.Scale(CaptionMetrics.TitleBarHeight, dpi),
        };

        // Without a non-client area the default handler would maximize to the whole monitor and hide
        // the taskbar, so the maximum size is the monitor work area.
        nint monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MonitorDefaultToNearest);
        if (monitor == 0)
        {
            Marshal.StructureToPtr(info, lParam, false);
            return;
        }

        MonitorInfo monitorInfo = new()
        {
            Size = Marshal.SizeOf<MonitorInfo>(),
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            Marshal.StructureToPtr(info, lParam, false);
            return;
        }

        NativeRect work = monitorInfo.Work;
        NativeRect bounds = monitorInfo.Monitor;
        MaximizeLimits limits = MaximizeLimits.FromWorkArea(
            bounds.Left,
            bounds.Top,
            work.Left,
            work.Top,
            work.Right,
            work.Bottom);
        info.MaxPosition = new NativePoint
        {
            X = limits.PositionX,
            Y = limits.PositionY,
        };
        info.MaxSize = new NativePoint
        {
            X = limits.SizeX,
            Y = limits.SizeY,
        };

        // The tracking limit stops an operator from dragging the window past the work area, which
        // would otherwise poke one row of pixels over the taskbar.
        info.MaxTrackSize = new NativePoint
        {
            X = limits.SizeX,
            Y = limits.SizeY,
        };

        Marshal.StructureToPtr(info, lParam, false);
    }


    private void Paint(nint handle)
    {
        nint deviceContext = NativeMethods.BeginPaint(handle, out PaintStruct paintStruct);
        if (deviceContext == 0)
        {
            return;
        }

        try
        {
            using Graphics graphics = Graphics.FromHdc(deviceContext);
            CaptionPainter.PaintClientArea(graphics, _metrics, BuildPaintRequest());
        }
        finally
        {
            _ = NativeMethods.EndPaint(handle, ref paintStruct);
        }
    }

    private CaptionPaintRequest BuildPaintRequest()
    {
        TitleBarPalette palette = TitleBarPalette.For(ThemeProbe.GetTheme(), _isActive);
        return new CaptionPaintRequest(palette, _status, _title, _hoveredButton, _pressedButton, IsMaximized);
    }

    private void UpdateHoverFromHitCode(int hitTestCode, nint handle)
    {
        CaptionButtonKind? hovered = CaptionHitTest.ButtonFromHitTestCode(hitTestCode);
        if (hovered != _hoveredButton)
        {
            _hoveredButton = hovered;
            RequestRepaint();
        }

        if (!_trackingMouseLeave)
        {
            TrackMouseEventRequest request = new()
            {
                Size = (uint)Marshal.SizeOf<TrackMouseEventRequest>(),
                Flags = NativeMethods.TrackMouseLeave | NativeMethods.TrackMouseNonClient,
                TrackWindow = handle,
                HoverTime = 0,
            };
            _trackingMouseLeave = NativeMethods.TrackMouseEvent(ref request);
        }
    }

    /// <summary>
    /// Handles a real non-client left-button press on one of the caption buttons.
    /// </summary>
    /// <remarks>
    /// The window covers its whole frame (<c>WM_NCCALCSIZE</c> answers zero), so the default
    /// procedure has no non-client button rectangle to press: it captures the pointer, paints the
    /// pressed look and then never completes the action. The buttons therefore run their own press
    /// cycle, while the hit test keeps answering the system codes so the window manager still offers
    /// Snap Layouts for the maximize button.
    /// </remarks>
    private nint PressCaptionButton(nint handle, int hitTestCode, nint packedPoint)
    {
        CaptionButtonKind? kind = CaptionHitTest.ButtonFromHitTestCode(hitTestCode);
        if (kind is null)
        {
            // The window has no non-client area at all, so the default procedure has nothing to grab: it
            // answers a caption hit code by looking for the frame it would have drawn, finds none, and the
            // button press ends without any window being captured. Dragging and resizing are therefore
            // started here, by handing the system the same command it would issue for a real frame.
            RequestWindowMoveOrResize(handle, hitTestCode);
            return 0;
        }

        _pressedButton = kind;
        _hoveredButton = kind;
        _ = NativeMethods.SetCapture(handle);
        RequestRepaint();
        return 0;
    }

    /// <summary>
    /// Starts the system's own move or resize loop for a press on the caption strip or a resize handle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DefWindowProc</c> cannot do this for this window. It answers <c>WM_NCLBUTTONDOWN</c> by capturing
    /// the pointer and starting the move loop for the non-client area it owns, and this window reports a
    /// zero non-client area so that the client area covers the whole frame. The press is then dropped: no
    /// window is captured, and the window can neither be dragged nor resized even though the hit test
    /// reports <c>HTCAPTION</c> and the style bits are all present.
    /// </para>
    /// <para>
    /// The move command must be <c>SC_MOVE | HTCAPTION</c>, the variant a real caption press produces:
    /// <c>SC_MOVE</c> alone is the keyboard form of the system-menu command, which parks the window in a
    /// mode that waits for arrow keys instead of following the held mouse button. The resize command's
    /// edge argument is computed by <see cref="WindowFrameHitTest.SystemSizeCommand"/>, because the
    /// <c>WMSZ</c> codes do not start where the hit-code family does.
    /// </para>
    /// <para>
    /// <c>lParam</c> must be zero. The system reads it as the cursor's screen position when the command
    /// came from a mouse press, so any other value — a window handle, for example — teleports the cursor
    /// to that packed point before the loop starts.
    /// </para>
    /// <para>
    /// Releasing the capture first matters: the system's loop takes the pointer itself, and a window that
    /// still holds the capture never sees the mouse move that would begin the drag.
    /// </para>
    /// </remarks>
    /// <param name="handle">The window that received the press.</param>
    /// <param name="hitTestCode">The hit code the press carried.</param>
    private static void RequestWindowMoveOrResize(nint handle, int hitTestCode)
    {
        int command;
        switch (hitTestCode)
        {
            case NativeMethods.HtCaption:
                command = NativeMethods.ScMove | NativeMethods.HtCaption;
                break;
            case NativeMethods.HtLeft:
            case NativeMethods.HtRight:
            case NativeMethods.HtTop:
            case NativeMethods.HtBottom:
            case NativeMethods.HtTopLeft:
            case NativeMethods.HtTopRight:
            case NativeMethods.HtBottomLeft:
            case NativeMethods.HtBottomRight:
                command = WindowFrameHitTest.SystemSizeCommand(hitTestCode);
                break;
            default:
                return;
        }

        _ = NativeMethods.ReleaseCapture();
        _ = NativeMethods.SendMessageW(handle, NativeMethods.WmSysCommand, command, 0);
    }

    /// <summary>Completes the press cycle after a non-client button release.</summary>
    private nint ReleaseCaptionButton(nint handle, int hitTestCode, nint packedPoint)
    {
        if (_pressedButton is null)
        {
            return NativeMethods.DefWindowProcW(handle, NativeMethods.WmNcLButtonUp, hitTestCode, packedPoint);
        }

        return CompletePressedButton(CaptionHitTest.ButtonFromHitTestCode(hitTestCode));
    }

    /// <summary>
    /// Completes the press cycle when the release arrives as a client message. A window with no
    /// non-client area can receive the release that way, and dropping it would leave the button stuck.
    /// </summary>
    private nint ReleaseCaptionButtonOnClientUp(nint handle, nint packedPoint)
    {
        if (_pressedButton is null)
        {
            // No caption press is in flight, so this is an ordinary client click and not ours.
            return NativeMethods.DefWindowProcW(handle, NativeMethods.WmLButtonUp, 0, packedPoint);
        }

        int x = NativeMethods.SignedLowWord(packedPoint);
        int y = NativeMethods.SignedHighWord(packedPoint);
        CaptionPart part = CaptionHitTest.Resolve(_metrics, x, y);
        return CompletePressedButton(CaptionHitTest.ToButtonKind(part));
    }

    private nint CompletePressedButton(CaptionButtonKind? releasedOver)
    {
        // The pressed kind has to be read before the capture is released: releasing it delivers
        // WM_CAPTURECHANGED inside this call, which cancels the press state.
        CaptionButtonKind pressed = _pressedButton
            ?? throw new InvalidOperationException("A caption press was completed without a pressed button.");

        _ = NativeMethods.ReleaseCapture();
        _pressedButton = null;
        _hoveredButton = releasedOver;
        RequestRepaint();

        if (releasedOver == pressed)
        {
            ExecuteCaptionButton(pressed);
        }

        return 0;
    }

    /// <summary>Drops a press whose button-up will never arrive, so no action fires later by accident.</summary>
    private void CancelPressedButton()
    {
        if (_pressedButton is null)
        {
            return;
        }

        _pressedButton = null;
        RequestRepaint();
    }

    /// <summary>
    /// Runs the action of one caption button through the system commands, so the result is the same
    /// as the one the window manager would have produced for a native caption.
    /// </summary>
    private void ExecuteCaptionButton(CaptionButtonKind kind)
    {
        nint handle = RequireHandle();
        switch (kind)
        {
            case CaptionButtonKind.Minimize:
                _ = NativeMethods.SendMessageW(handle, NativeMethods.WmSysCommand, NativeMethods.ScMinimize, 0);
                break;
            case CaptionButtonKind.Maximize:
                int command = NativeMethods.IsZoomed(handle) ? NativeMethods.ScRestore : NativeMethods.ScMaximize;
                _ = NativeMethods.SendMessageW(handle, NativeMethods.WmSysCommand, command, 0);
                break;
            case CaptionButtonKind.Close:
                // WM_CLOSE is the single close entry point, so the confirmation always runs first.
                _ = NativeMethods.SendMessageW(handle, NativeMethods.WmClose, 0, 0);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown caption button.");
        }
    }

    private void ClearPointerState()
    {
        _trackingMouseLeave = false;
        if (_hoveredButton is null && _pressedButton is null)
        {
            return;
        }

        _hoveredButton = null;
        _pressedButton = null;
        RequestRepaint();
    }

    private void UpdateActivation(bool isActive)
    {
        if (_isActive == isActive)
        {
            return;
        }

        _isActive = isActive;
        RequestRepaint();
    }

    private void RecomputeMetrics(nint handle)
    {
        int clientWidth = ReadClientWidth(handle);
        int dpi = DpiProbe.GetWindowDpi(handle);
        if (clientWidth < CaptionMetrics.Scale(CaptionMetrics.MinimumClientWidth, dpi))
        {
            // WM_GETMINMAXINFO already stops the window from getting this narrow; keeping the last
            // layout is the only outcome that cannot throw inside the message dispatch.
            RequestRepaint();
            return;
        }

        _metrics = CaptionMetrics.Create(clientWidth, dpi, TextScaleProbe.GetTextScalePercent());
        RecomputePictureBounds();
        RepositionPictureSurface();
        RequestRepaint();
    }

    /// <summary>Recomputes where the picture goes; the rest of the client area is the black letterbox.</summary>
    /// <remarks>
    /// The picture is fitted into the area below the caption strip, never into the whole client area. The
    /// caption is painted inside the client area, so a rectangle measured from its top would put the remote
    /// desktop over the title bar: the window would then look like it had no title bar at all, and the
    /// eight-pixel resize border would be swallowed by the control, which makes the window impossible to
    /// drag or resize.
    /// </remarks>
    private void RecomputePictureBounds()
    {
        if (_sessionWidth <= 0 || _sessionHeight <= 0)
        {
            PictureBounds = Rectangle.Empty;
            return;
        }

        if (!NativeMethods.GetClientRect(_handle, out NativeRect clientRectangle))
        {
            PictureBounds = Rectangle.Empty;
            return;
        }

        int clientWidth = clientRectangle.Right - clientRectangle.Left;
        int clientHeight = clientRectangle.Bottom - clientRectangle.Top;
        int captionHeight = _metrics.Height;

        // The resize border is kept reachable on every side. A child window owns every point inside its
        // rectangle, so a picture that reaches the window edge takes the hit test with it and the window
        // can no longer be resized or, along the top, dragged.
        int border = CaptionMetrics.Scale(WindowFrameHitTest.ResizeBorderPixels, _metrics.Dpi);
        int availableHeight = clientHeight - captionHeight - border;
        int availableWidth = clientWidth - (border * 2);
        if (availableHeight <= 0 || availableWidth <= 0)
        {
            PictureBounds = Rectangle.Empty;
            return;
        }

        Rectangle fitted = DesktopPictureLayout.Fit(availableWidth, availableHeight, _sessionWidth, _sessionHeight);
        PictureBounds = new Rectangle(
            fitted.Left + border,
            fitted.Top + captionHeight,
            fitted.Width,
            fitted.Height);
    }

    /// <summary>Puts the picture surface exactly on the picture rectangle, never on the letterbox.</summary>
    private void RepositionPictureSurface()
    {
        if (_pictureSurface == 0 || _adoptPicture is null)
        {
            return;
        }

        _adoptPicture(_handle, PictureBounds);
    }

    private void ApplyDpiChange(nint handle, nint wParam, nint lParam)
    {
        int dpi = NativeMethods.SignedLowWord(wParam);
        if (dpi <= 0)
        {
            return;
        }

        NativeRect suggested = default;
        bool hasSuggested = lParam != 0;
        if (hasSuggested)
        {
            suggested = Marshal.PtrToStructure<NativeRect>(lParam);
        }

        // Apply the suggested rectangle before measuring the new layout. A narrow window moved onto a
        // higher-DPI monitor can otherwise be measured below the new minimum width, which would throw
        // out of the window procedure instead of repainting the caption.
        int minimumWidth = CaptionMetrics.Scale(CaptionMetrics.MinimumClientWidth, dpi);
        if (hasSuggested)
        {
            int suggestedWidth = suggested.Right - suggested.Left;
            int suggestedHeight = suggested.Bottom - suggested.Top;
            _ = NativeMethods.SetWindowPos(
                handle,
                0,
                suggested.Left,
                suggested.Top,
                Math.Max(suggestedWidth, minimumWidth),
                suggestedHeight,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        }

        int clientWidth;
        try
        {
            clientWidth = ReadClientWidth(handle);
        }
        catch (InvalidOperationException)
        {
            RequestRepaint();
            return;
        }

        if (clientWidth < minimumWidth)
        {
            // The window could not be widened to the new minimum; keep the previous layout rather
            // than letting CaptionMetrics throw through the native message dispatch.
            RequestRepaint();
            return;
        }

        try
        {
            _metrics = CaptionMetrics.Create(clientWidth, dpi, TextScaleProbe.GetTextScalePercent());
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            RequestRepaint();
            return;
        }

        RecomputePictureBounds();
        RepositionPictureSurface();
        RequestRepaint();
    }

    private void RequestClose(nint handle)
    {
        bool confirmed;
        try
        {
            confirmed = _confirmClose();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A failed confirmation must never unwind through the window procedure. Keep the window
            // alive so the operator can retry after the dialog failure is fixed.
            Console.Error.WriteLine(
                "E_MANUAL_INTERVENTION The close confirmation could not be shown ("
                + error.GetType().Name
                + "), so the window remains open.");
            return;
        }

        if (!confirmed)
        {
            return;
        }

        _ = NativeMethods.DestroyWindow(handle);
    }

    private void RequestRepaint()
    {
        if (_handle == 0 || _destroyed)
        {
            return;
        }

        // A repaint request is best effort: a window that is going away cannot fail anything the
        // caller can act on, and the next WM_PAINT paints the whole client area anyway.
        _ = NativeMethods.InvalidateRect(_handle, 0, false);
    }

    private nint RequireHandle()
    {
        if (_handle == 0)
        {
            throw new ObjectDisposedException(nameof(ProductWindow), "The window was already destroyed.");
        }

        return _handle;
    }

    private static int ReadClientWidth(nint handle)
    {
        if (!NativeMethods.GetClientRect(handle, out NativeRect clientRectangle))
        {
            throw NativeMethods.InteropFailure("GetClientRect", Marshal.GetLastWin32Error());
        }

        return clientRectangle.Right - clientRectangle.Left;
    }

    private static Rectangle ReadWindowRectangle(nint handle)
    {
        if (!NativeMethods.GetWindowRect(handle, out NativeRect windowRectangle))
        {
            throw NativeMethods.InteropFailure("GetWindowRect", Marshal.GetLastWin32Error());
        }

        return Rectangle.FromLTRB(windowRectangle.Left, windowRectangle.Top, windowRectangle.Right, windowRectangle.Bottom);
    }

    private static ushort EnsureClassRegistered()
    {
        if (_classAtom != 0)
        {
            return _classAtom;
        }

        nint instance = NativeMethods.GetModuleHandleW(null);
        if (instance == 0)
        {
            throw NativeMethods.InteropFailure("GetModuleHandleW", Marshal.GetLastWin32Error());
        }

        WindowClassEx windowClass = new()
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            Style = 0,
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(WindowDelegate),
            ClassExtraBytes = 0,
            WindowExtraBytes = 0,
            Instance = instance,
            Icon = BrandIcon.Handle,
            Cursor = NativeMethods.LoadArrowCursor(),
            BackgroundBrush = 0,
            MenuName = null,
            ClassName = WindowClassName,
            SmallIcon = BrandIcon.Handle,
        };
        _classAtom = NativeMethods.RegisterClassExW(ref windowClass);
        if (_classAtom == 0)
        {
            throw NativeMethods.InteropFailure($"RegisterClassExW(class={WindowClassName})", Marshal.GetLastWin32Error());
        }

        return _classAtom;
    }

    private delegate nint WindowProcedure(nint handle, uint message, nint wParam, nint lParam);
}
