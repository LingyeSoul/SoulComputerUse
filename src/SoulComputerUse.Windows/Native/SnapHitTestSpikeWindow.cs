using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Runtime.InteropServices;

namespace SoulComputerUse.Windows.Native;

/// <summary>
/// M0 technical gate for Snap Layouts: a real top-level window whose <c>WM_NCHITTEST</c>
/// handler answers <c>HTMAXBUTTON</c> for two deliberately placed hot zones, one on the
/// system title bar and one inside the client area. If Windows 11 shows the Snap Layouts
/// flyout for both, a self-drawn caption can be trusted to keep Snap working.
/// </summary>
/// <remarks>
/// This is throwaway gate code, not the product window: it keeps the standard non-client
/// frame so the experiment cannot be confounded by an unfinished custom caption. At most
/// one window may be open per process, which is all the gate needs.
/// </remarks>
public sealed class SnapHitTestSpikeWindow : IDisposable
{
    /// <summary>The hit-test code that makes Windows offer Snap Layouts.</summary>
    public const int MaxButtonHitTestCode = NativeMethods.HtMaxButton;

    /// <summary>The hit-test code a normal client-area point resolves to.</summary>
    public const int ClientHitTestCode = NativeMethods.HtClient;

    private const string SpikeClassName = "SoulComputerUse.SnapHitTestSpikeWindow";
    // The title-bar zone sits over the window icon at the far left, so a human can find it
    // without any marking; the client-area zone is painted with its own label.
    private const int WindowTitleZoneLeft = 2;
    private const int WindowTitleZoneWidth = 60;
    private const int WindowTitleZoneHeight = 22;
    private const int WindowClientZoneLeft = 24;
    private const int WindowClientZoneTop = 24;
    private const int WindowClientZoneWidth = 220;
    private const int WindowClientZoneHeight = 40;

    private static readonly WindowProcedure WindowDelegate = HandleWindowMessage;
    private static readonly object RegistrationLock = new();
    private static readonly Dictionary<nint, SnapHitTestSpikeWindow> OpenWindows = new();

    private static ushort _classAtom;

    private readonly Point _clientOriginInWindow;
    private nint _handle;
    private string _dpiDescription = string.Empty;
    private bool _destroyed;
    private bool _disposed;

    private SnapHitTestSpikeWindow(nint handle, Point clientOriginInWindow, Rectangle titleBarHotZone)
    {
        _handle = handle;
        _clientOriginInWindow = clientOriginInWindow;
        TitleBarHotZone = titleBarHotZone;
        ClientHotZone = new Rectangle(WindowClientZoneLeft, WindowClientZoneTop, WindowClientZoneWidth, WindowClientZoneHeight);
    }

    /// <summary>The hot zone expressed in window coordinates, over the system title bar.</summary>
    public Rectangle TitleBarHotZone { get; }

    /// <summary>The hot zone expressed in client coordinates, marked by painted text.</summary>
    public Rectangle ClientHotZone { get; }

    /// <summary>The window handle, or <see cref="nint.Zero"/> once the window was destroyed.</summary>
    public nint Handle => _handle;

    /// <summary>True once the window received <c>WM_DESTROY</c>.</summary>
    public bool IsDestroyed => _destroyed;

    /// <summary>The window rectangle in screen coordinates.</summary>
    public Rectangle WindowRectangleInScreen
    {
        get
        {
            NativeRect windowRect = ReadWindowRect(RequireHandle());
            return Rectangle.FromLTRB(windowRect.Left, windowRect.Top, windowRect.Right, windowRect.Bottom);
        }
    }

    /// <summary>The client-area origin in screen coordinates.</summary>
    public Point ClientOriginInScreen
    {
        get
        {
            Rectangle windowRectangle = WindowRectangleInScreen;
            return new Point(windowRectangle.Left + _clientOriginInWindow.X, windowRectangle.Top + _clientOriginInWindow.Y);
        }
    }

    /// <summary>Creates the spike window with the requested client size and shows it.</summary>
    /// <summary>Creates a spike window that keeps the native system caption.</summary>
    public static SnapHitTestSpikeWindow Create(int clientWidth, int clientHeight)
    {
        return CreateWindow(
            clientWidth,
            clientHeight,
            NativeMethods.WsOverlappedWindow,
            "B — 系统标题栏 + 客户区热区",
            new Rectangle(WindowTitleZoneLeft, 0, WindowTitleZoneWidth, WindowTitleZoneHeight));
    }

    /// <summary>
    /// Creates a spike window with no system caption at all, which is the shape the product window
    /// will have: the caption is drawn by the app and the only source of truth about the maximize
    /// button is the window's own hit test.
    /// </summary>
    public static SnapHitTestSpikeWindow CreateWithoutSystemCaption(int clientWidth, int clientHeight)
    {
        return CreateWindow(
            clientWidth,
            clientHeight,
            NativeMethods.WsOverlappedWindow & ~NativeMethods.WsCaption,
            "C — 无系统标题栏 + 客户区热区",
            Rectangle.Empty);
    }

    private static SnapHitTestSpikeWindow CreateWindow(
        int clientWidth,
        int clientHeight,
        uint style,
        string title,
        Rectangle titleBarHotZone)
    {
        if (clientWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clientWidth), clientWidth, "The client width must be positive.");
        }

        if (clientHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clientHeight), clientHeight, "The client height must be positive.");
        }

        lock (RegistrationLock)
        {
            ushort classAtom = EnsureClassRegistered();
            NativeRect windowRect = new()
            {
                Left = 0,
                Top = 0,
                Right = clientWidth,
                Bottom = clientHeight,
            };
            if (!NativeMethods.AdjustWindowRectEx(ref windowRect, style, false, 0))
            {
                throw Win32Failure("AdjustWindowRectEx", Marshal.GetLastWin32Error());
            }

            int width = windowRect.Right - windowRect.Left;
            int height = windowRect.Bottom - windowRect.Top;
            nint handle = NativeMethods.CreateWindowExW(
                0,
                SpikeClassName,
                title,
                style | NativeMethods.WsVisible,
                NativeMethods.CwUseDefault,
                NativeMethods.CwUseDefault,
                width,
                height,
                0,
                0,
                NativeMethods.GetModuleHandleW(null),
                0);
            if (handle == 0)
            {
                throw Win32Failure($"CreateWindowExW(class={SpikeClassName}, atom={classAtom})", Marshal.GetLastWin32Error());
            }

            SnapHitTestSpikeWindow window = new(handle, ReadClientOriginInWindow(handle), titleBarHotZone);
            // Captured at creation so the paint handler never has to make a fallible interop call.
            window._dpiDescription = DpiProbe.Describe(handle);
            OpenWindows[handle] = window;
            return window;
        }
    }

    /// <summary>Sends a real <c>WM_NCHITTEST</c> for a screen point and returns the hit code.</summary>
    public int ProbeHitTest(int screenX, int screenY)
    {
        nint handle = RequireHandle();
        return (int)NativeMethods.SendMessageW(handle, NativeMethods.WmNcHitTest, 0, NativeMethods.PackWords(screenX, screenY));
    }

    /// <summary>Sends a real <c>WM_NCHITTEST</c> for the centre of a zone given in screen coordinates.</summary>
    public int ProbeCentreOfMaximizeZone(Rectangle zoneInScreen)
    {
        return ProbeHitTest(zoneInScreen.Left + (zoneInScreen.Width / 2), zoneInScreen.Top + (zoneInScreen.Height / 2));
    }

    /// <summary>Destroys the window if it still exists.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle != 0 && !_destroyed)
        {
            NativeMethods.DestroyWindow(_handle);
        }

        nint handle = _handle;
        _handle = 0;
        lock (RegistrationLock)
        {
            OpenWindows.Remove(handle);
        }
    }

    private static nint HandleWindowMessage(nint handle, uint message, nint wParam, nint lParam)
    {
        OpenWindows.TryGetValue(handle, out SnapHitTestSpikeWindow? window);
        switch (message)
        {
            case NativeMethods.WmNcHitTest when window is not null:
                return window.ResolveHitTest(handle, lParam);
            case NativeMethods.WmNcCalcSize when window is not null && window.TitleBarHotZone.IsEmpty:
                // Returning zero with wParam = TRUE makes the client area cover the whole window.
                // Stripping WS_CAPTION alone is not enough: the window manager still draws a
                // caption, which is exactly what this variant has to avoid.
                return 0;
            case NativeMethods.WmNcPaint when window is not null:
                return window.PaintTitleBarHotZone(handle, wParam, lParam);
            case NativeMethods.WmPaint when window is not null:
                window.PaintClientHotZone(handle);
                return 0;
            case NativeMethods.WmDestroy:
                if (window is not null)
                {
                    window._destroyed = true;
                }

                NativeMethods.PostQuitMessage(0);
                return 0;
            default:
                return NativeMethods.DefWindowProcW(handle, message, wParam, lParam);
        }
    }

    /// <summary>
    /// A window procedure must never throw: an exception would unwind through the OS
    /// message dispatch. The one fallible probe therefore reports "no hit" instead.
    /// </summary>
    private nint ResolveHitTest(nint handle, nint packedPoint)
    {
        if (!NativeMethods.GetWindowRect(handle, out NativeRect windowRect))
        {
            return 0;
        }

        int windowX = NativeMethods.SignedLowWord(packedPoint) - windowRect.Left;
        int windowY = NativeMethods.SignedHighWord(packedPoint) - windowRect.Top;
        if (!TitleBarHotZone.IsEmpty && TitleBarHotZone.Contains(windowX, windowY))
        {
            return NativeMethods.HtMaxButton;
        }

        if (ClientHotZone.Contains(windowX - _clientOriginInWindow.X, windowY - _clientOriginInWindow.Y))
        {
            return NativeMethods.HtMaxButton;
        }

        return NativeMethods.DefWindowProcW(handle, NativeMethods.WmNcHitTest, 0, packedPoint);
    }

    private void PaintClientHotZone(nint handle)
    {
        nint deviceContext = NativeMethods.BeginPaint(handle, out PaintStruct paintStruct);
        if (deviceContext == 0)
        {
            return;
        }

        try
        {
            using Graphics graphics = Graphics.FromHdc(deviceContext);
            using SolidBrush fill = new(Color.FromArgb(64, 78, 166, 240));
            using Pen border = new(Color.FromArgb(255, 78, 166, 240), 2.0f);
            using Font labelFont = new("Segoe UI", 9.5f, FontStyle.Bold);
            using Font bodyFont = new("Microsoft YaHei UI", 11.0f);
            using SolidBrush text = new(Color.FromArgb(255, 27, 27, 27));
            graphics.FillRectangle(fill, ClientHotZone);
            graphics.DrawRectangle(border, ClientHotZone);
            graphics.DrawString("zone 1 - client area (HTMAXBUTTON)", labelFont, text, ClientHotZone.Left + 8.0f, ClientHotZone.Top + 11.0f);
            string captionLine = TitleBarHotZone.IsEmpty
                ? "本窗口没有系统标题栏（整块都是客户区）"
                : "本窗口保留系统标题栏（但热区在客户区里）";
            graphics.DrawString(
                "M0 Snap 对照实验\r\n" +
                captionLine + " · " + _dpiDescription + "\r\n" +
                "把鼠标各停 0.5 秒，看系统弹不弹「贴靠布局」面板：\r\n" +
                "  ① 上面这个蓝框\r\n" +
                "  ② 对照窗口 A 右上角的最大化按钮（系统自己画的）\r\n" +
                "蓝框弹 = 自绘标题栏能保住 Snap；只有 A 弹 = 我们的命中方案还得改",
                bodyFont,
                text,
                ClientHotZone.Left,
                ClientHotZone.Bottom + 40.0f);
        }
        finally
        {
            NativeMethods.EndPaint(handle, ref paintStruct);
        }
    }

    /// <summary>
    /// Draws the title-bar hot zone over the system caption. The default frame is painted first,
    /// then this marker is drawn on top of it through a window device context.
    /// </summary>
    [SuppressMessage(
        "Performance",
        "CA1806:Do not ignore method results",
        Justification = "A window procedure must never throw, and a window DC that refuses to be released is reclaimed by the system when the window is destroyed.")]
    private nint PaintTitleBarHotZone(nint handle, nint wParam, nint lParam)
    {
        nint frameResult = NativeMethods.DefWindowProcW(handle, NativeMethods.WmNcPaint, wParam, lParam);
        if (TitleBarHotZone.IsEmpty)
        {
            return frameResult;
        }

        nint deviceContext = NativeMethods.GetWindowDeviceContext(handle);
        if (deviceContext == 0)
        {
            return frameResult;
        }

        try
        {
            using Graphics graphics = Graphics.FromHdc(deviceContext);
            using SolidBrush fill = new(Color.FromArgb(210, 249, 168, 37));
            using Pen border = new(Color.FromArgb(255, 176, 106, 8), 1.0f);
            graphics.FillRectangle(fill, TitleBarHotZone);
            graphics.DrawRectangle(
                border,
                TitleBarHotZone.Left,
                TitleBarHotZone.Top,
                TitleBarHotZone.Width - 1,
                TitleBarHotZone.Height - 1);
        }
        finally
        {
            NativeMethods.ReleaseDeviceContext(handle, deviceContext);
        }

        return frameResult;
    }

    private nint RequireHandle()
    {
        if (_handle == 0)
        {
            throw new ObjectDisposedException(nameof(SnapHitTestSpikeWindow), "The spike window was already destroyed.");
        }

        return _handle;
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
            throw Win32Failure("GetModuleHandleW", Marshal.GetLastWin32Error());
        }

        // Without a class background brush the client surface keeps stale pixels from whatever the
        // screen showed when the window was created, which makes the painted hints unreadable.
        nint backgroundBrush = NativeMethods.GetStockObject(NativeMethods.WhiteBrush);
        if (backgroundBrush == 0)
        {
            throw Win32Failure("GetStockObject(WHITE_BRUSH)", Marshal.GetLastWin32Error());
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
            BackgroundBrush = backgroundBrush,
            MenuName = null,
            ClassName = SpikeClassName,
            SmallIcon = 0,
        };
        _classAtom = NativeMethods.RegisterClassExW(ref windowClass);
        if (_classAtom == 0)
        {
            throw Win32Failure($"RegisterClassExW(class={SpikeClassName})", Marshal.GetLastWin32Error());
        }

        return _classAtom;
    }

    private static Point ReadClientOriginInWindow(nint handle)
    {
        NativeRect windowRect = ReadWindowRect(handle);
        NativePoint clientOrigin = new()
        {
            X = 0,
            Y = 0,
        };
        if (!NativeMethods.ClientToScreen(handle, ref clientOrigin))
        {
            throw Win32Failure("ClientToScreen", Marshal.GetLastWin32Error());
        }

        return new Point(clientOrigin.X - windowRect.Left, clientOrigin.Y - windowRect.Top);
    }

    private static NativeRect ReadWindowRect(nint handle)
    {
        if (!NativeMethods.GetWindowRect(handle, out NativeRect windowRect))
        {
            throw Win32Failure("GetWindowRect", Marshal.GetLastWin32Error());
        }

        return windowRect;
    }

    private static InvalidOperationException Win32Failure(string operation, int errorCode)
    {
        return NativeMethods.InteropFailure(operation, errorCode);
    }

    private delegate nint WindowProcedure(nint handle, uint message, nint wParam, nint lParam);
}