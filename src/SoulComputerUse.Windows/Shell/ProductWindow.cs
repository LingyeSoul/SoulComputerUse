using System;
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
/// (<c>HTMINBUTTON</c>, <c>HTMAXBUTTON</c>, <c>HTCLOSE</c>), so the window manager performs every
/// action and all four close routes converge on <c>WM_CLOSE</c>. The hover and pressed visuals are
/// painted here from the tracking messages that follow those codes.
/// </para>
/// </remarks>
public sealed class ProductWindow : IDisposable
{
    /// <summary>The registered window class name.</summary>
    public const string WindowClassName = "SoulComputerUse.ProductWindow";


    private static readonly object RegistrationLock = new();
    private static readonly Dictionary<nint, ProductWindow> OpenWindows = new();
    private static readonly WindowProcedure WindowDelegate = HandleWindowMessage;

    private static ushort _classAtom;

    private readonly Func<bool> _confirmClose;

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
    /// Attaches the window that renders the desktop picture. It becomes a child of this window and is
    /// placed exactly on <see cref="PictureBounds"/>, so the letterbox is never painted over.
    /// </summary>
    public void AttachPictureSurface(nint surface)
    {
        if (surface == 0)
        {
            throw new ArgumentException("The picture surface handle must not be zero.", nameof(surface));
        }

        nint handle = RequireHandle();
        _ = NativeMethods.SetWindowLongPtrW(surface, NativeMethods.GwlpStyle, (nint)NativeMethods.WsChild);
        _ = NativeMethods.SetParent(surface, handle);
        _pictureSurface = surface;
        RepositionPictureSurface();
    }

    /// <summary>Detaches the picture surface without destroying it.</summary>
    public void DetachPictureSurface()
    {
        if (_pictureSurface != 0)
        {
            _ = NativeMethods.SetParent(_pictureSurface, 0);
        }

        _pictureSurface = 0;
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
                // non-client message; the visual feedback is still drawn by this window, and the
                // action itself is left to the default procedure.
                window.UpdateHoverFromHitCode((int)wParam, handle);
                return NativeMethods.DefWindowProcW(handle, message, wParam, lParam);
            case NativeMethods.WmNcLButtonDown when window is not null:
                window.PressButtonFromHitCode((int)wParam);
                return NativeMethods.DefWindowProcW(handle, message, wParam, lParam);
            case NativeMethods.WmNcLButtonUp when window is not null:
                window.ReleaseButtonFromHitCode((int)wParam);
                return NativeMethods.DefWindowProcW(handle, message, wParam, lParam);
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
            case NativeMethods.WmClose when window is not null:
                window.RequestClose(handle);
                return 0;
            case NativeMethods.WmDestroy:
                if (window is not null)
                {
                    window._destroyed = true;
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

    private void PressButtonFromHitCode(int hitTestCode)
    {
        _pressedButton = CaptionHitTest.ButtonFromHitTestCode(hitTestCode);
        RequestRepaint();
    }

    private void ReleaseButtonFromHitCode(int hitTestCode)
    {
        _pressedButton = null;
        _hoveredButton = CaptionHitTest.ButtonFromHitTestCode(hitTestCode);
        RequestRepaint();
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

        PictureBounds = DesktopPictureLayout.Fit(
            clientRectangle.Right - clientRectangle.Left,
            clientRectangle.Bottom - clientRectangle.Top,
            _sessionWidth,
            _sessionHeight);
    }

    /// <summary>Puts the picture surface exactly on the picture rectangle, never on the letterbox.</summary>
    private void RepositionPictureSurface()
    {
        if (_pictureSurface == 0 || PictureBounds.IsEmpty)
        {
            return;
        }

        _ = NativeMethods.SetWindowPos(
            _pictureSurface,
            0,
            PictureBounds.Left,
            PictureBounds.Top,
            PictureBounds.Width,
            PictureBounds.Height,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    private void ApplyDpiChange(nint handle, nint wParam, nint lParam)
    {
        int dpi = NativeMethods.SignedLowWord(wParam);
        if (dpi <= 0)
        {
            return;
        }

        _metrics = CaptionMetrics.Create(ReadClientWidth(handle), dpi, TextScaleProbe.GetTextScalePercent());
        RecomputePictureBounds();
        RepositionPictureSurface();
        if (lParam != 0)
        {
            NativeRect suggested = Marshal.PtrToStructure<NativeRect>(lParam);
            _ = NativeMethods.SetWindowPos(
                handle,
                0,
                suggested.Left,
                suggested.Top,
                suggested.Right - suggested.Left,
                suggested.Bottom - suggested.Top,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        }

        RequestRepaint();
    }

    private void RequestClose(nint handle)
    {
        if (!_confirmClose())
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
            Icon = 0,
            Cursor = NativeMethods.LoadArrowCursor(),
            BackgroundBrush = 0,
            MenuName = null,
            ClassName = WindowClassName,
            SmallIcon = 0,
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
