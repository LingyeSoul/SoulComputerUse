using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SoulComputerUse.Windows.Native;

/// <summary>
/// The complete user32/kernel32 surface the host window needs. Interop lives here and
/// nowhere else so every other type stays free of raw P/Invoke declarations.
/// </summary>
internal static class NativeMethods
{
    internal const uint WmDestroy = 0x0002;
    internal const uint WmSize = 0x0005;
    internal const uint WmActivate = 0x0006;
    internal const uint WmClose = 0x0010;
    internal const uint WmEraseBkgnd = 0x0014;
    internal const uint WmPaint = 0x000F;
    internal const uint WmNcActivate = 0x0086;
    internal const uint WmNcHitTest = 0x0084;
    internal const uint WmNcPaint = 0x0085;
    internal const uint WmNcCalcSize = 0x0083;
    internal const uint WmNcMouseMove = 0x00A0;
    internal const uint WmNcLButtonDown = 0x00A1;
    internal const uint WmNcLButtonUp = 0x00A2;
    internal const uint WmNcMouseLeave = 0x02A2;
    internal const uint WmCaptureChanged = 0x0215;
    internal const uint WmLButtonUp = 0x0202;
    internal const uint WmDpiChanged = 0x02E0;
    internal const uint WmGetMinMaxInfo = 0x0024;

    internal const uint WmSysCommand = 0x0112;
    internal const int ScMinimize = 0xF020;
    internal const int ScMaximize = 0xF030;
    internal const int ScRestore = 0xF120;
    internal const int ScClose = 0xF060;

    internal const uint TrackMouseLeave = 0x0002;
    internal const uint TrackMouseNonClient = 0x0010;

    internal const int GwlpStyle = -16;
    internal const long WsChild = 0x40000000;

    internal const int HtClient = 1;
    internal const int HtCaption = 2;
    internal const int HtMinButton = 8;
    internal const int HtMaxButton = 9;
    internal const int HtLeft = 10;
    internal const int HtRight = 11;
    internal const int HtTop = 12;
    internal const int HtTopLeft = 13;
    internal const int HtTopRight = 14;
    internal const int HtBottom = 15;
    internal const int HtBottomLeft = 16;
    internal const int HtBottomRight = 17;
    internal const int HtClose = 20;

    internal const int MonitorDefaultToNearest = 2;
    internal const uint PmRemove = 0x0001;

    internal const uint WsOverlappedWindow = 0x00CF0000;
    internal const uint WsCaption = 0x00C00000;
    internal const uint WsMaximizeBox = 0x00010000;
    internal const uint WsThickFrame = 0x00040000;
    internal const uint WsVisible = 0x10000000;
    internal const int CwUseDefault = unchecked((int)0x80000000);
    internal const int IdiArrow = 32512;
    internal const int WhiteBrush = 0;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpFrameChanged = 0x0020;

    internal const int SmCxScreen = 0;
    internal const int SmCyScreen = 1;
    internal const int ErrorAlreadyExists = 183;
    internal const int ErrorFileNotFound = 2;
    internal const uint Synchronize = 0x00100000;

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern ushort RegisterClassExW(ref WindowClassEx windowClass);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern nint CreateWindowExW(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", SetLastError = true)]
    internal static extern nint DefWindowProcW(nint handle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint handle);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetParent(nint child, nint newParent);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern nint SetWindowLongPtrW(nint handle, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetParent(nint handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint handle, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(nint handle, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AdjustWindowRectEx(ref NativeRect rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint exStyle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ScreenToClient(nint handle, ref NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClientToScreen(nint handle, ref NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint BeginPaint(nint handle, out PaintStruct paintStruct);

    [DllImport("user32.dll", EntryPoint = "GetWindowDC", SetLastError = true)]
    internal static extern nint GetWindowDeviceContext(nint handle);

    [DllImport("user32.dll", EntryPoint = "ReleaseDC", SetLastError = true)]
    internal static extern int ReleaseDeviceContext(nint handle, nint deviceContext);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EndPaint(nint handle, ref PaintStruct paintStruct);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", SetLastError = true)]
    internal static extern nint SendMessageW(nint handle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessageW(nint handle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessageW(out Msg message, nint handle, uint filterMinimum, uint filterMaximum, uint removeMessage);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    internal static extern int GetMessageW(out Msg message, nint handle, uint filterMinimum, uint filterMaximum);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW", SetLastError = true)]
    internal static extern nint DispatchMessageW(ref Msg message);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", EntryPoint = "LoadCursorW", SetLastError = true)]
    internal static extern nint LoadCursorW(nint instance, nint cursorName);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("kernel32.dll", EntryPoint = "CreateMutexW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern nint CreateMutexW(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool initialOwner, string name);

    [DllImport("kernel32.dll", EntryPoint = "OpenMutexW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern nint OpenMutexW(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GetLastError();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", EntryPoint = "GetExitCodeProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(nint process, out int exitCode);

    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShellExecuteExW(ref ShellExecuteInfo executeInfo);

    [DllImport("gdi32.dll", EntryPoint = "GetStockObject", SetLastError = true)]
    internal static extern nint GetStockObject(int index);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ProcessIdToSessionId(uint processId, out int sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", EntryPoint = "WTSIsChildSessionsEnabled", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSIsChildSessionsEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnableChildSessions", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSEnableChildSessions([MarshalAs(UnmanagedType.Bool)] bool enabled);

    // WTS_CURRENT_SESSION and WTS_INFO_CLASS.WTSSessionInfo from wtsapi32.h.
    internal const uint WtsCurrentSession = 0xFFFFFFFF;
    internal const int WtsInfoClassSessionInfo = 24;

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSQuerySessionInformationW(
        nint server,
        uint sessionId,
        int infoClass,
        out nint buffer,
        out int byteCount);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSFreeMemory", SetLastError = true)]
    internal static extern void WTSFreeMemory(nint memory);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetDpiForWindow(nint handle);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetAwarenessFromDpiAwarenessContext(nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsZoomed(nint handle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern nint GetWindowLongPtrW(nint handle, int index);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetCapture(nint handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InvalidateRect(nint handle, nint rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateWindow(nint handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TrackMouseEvent(ref TrackMouseEventRequest request);

    [DllImport("user32.dll", EntryPoint = "SetWindowTextW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowTextW(nint handle, string text);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint MonitorFromWindow(nint handle, int flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    /// <summary>Builds one explicit failure for a failed interop call, including the Win32 code.</summary>
    internal static InvalidOperationException InteropFailure(string operation, int errorCode)
    {
        return new InvalidOperationException(
            $"{operation} failed with Win32 error {errorCode} ({new Win32Exception(errorCode).Message}).");
    }

    /// <summary>Unpacks the signed low 16 bits of a message parameter.</summary>
    internal static int SignedLowWord(nint packed)
    {
        return unchecked((short)((long)packed & 0xFFFF));
    }

    /// <summary>Unpacks the signed high 16 bits of a message parameter.</summary>
    internal static int SignedHighWord(nint packed)
    {
        return unchecked((short)(((long)packed >> 16) & 0xFFFF));
    }

    /// <summary>Packs two 16-bit values into a message parameter, as lParam expects.</summary>
    internal static nint PackWords(int low, int high)
    {
        long packed = ((long)(high & 0xFFFF) << 16) | (uint)(low & 0xFFFF);
        return (nint)packed;
    }

    /// <summary>Loads the standard arrow cursor, falling back to the class default.</summary>
    internal static nint LoadArrowCursor()
    {
        return LoadCursorW(0, (nint)IdiArrow);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    internal int X;
    internal int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    internal int Left;
    internal int Top;
    internal int Right;
    internal int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PaintStruct
{
    internal nint DeviceContext;
    internal int Erase;
    internal NativeRect Paint;
    internal int Restore;
    internal int IncUpdate;
    internal PaintStructReserved Reserved;
}

[InlineArray(32)]
internal struct PaintStructReserved
{
    private byte _first;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ShellExecuteInfo
{
    internal int Size;
    internal uint Mask;
    internal nint Window;
    internal string? Verb;
    internal string? File;
    internal string? Parameters;
    internal string? Directory;
    internal int Show;
    internal nint Instance;
    internal nint IdList;
    internal string? Class;
    internal nint ClassKey;
    internal uint HotKey;
    internal nint IconOrMonitor;
    internal nint Process;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WindowClassEx
{
    internal uint Size;
    internal uint Style;
    internal nint WindowProcedure;
    internal int ClassExtraBytes;
    internal int WindowExtraBytes;
    internal nint Instance;
    internal nint Icon;
    internal nint Cursor;
    internal nint BackgroundBrush;
    internal string? MenuName;
    internal string ClassName;
    internal nint SmallIcon;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TrackMouseEventRequest
{
    internal uint Size;
    internal uint Flags;
    internal nint TrackWindow;
    internal uint HoverTime;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MinMaxInfo
{
    internal NativePoint Reserved;
    internal NativePoint MaxSize;
    internal NativePoint MaxPosition;
    internal NativePoint MinTrackSize;
    internal NativePoint MaxTrackSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MonitorInfo
{
    internal int Size;
    internal NativeRect Monitor;
    internal NativeRect Work;
    internal uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NcCalcSizeParams
{
    internal NativeRect Proposed;
    internal NativeRect Before;
    internal NativeRect After;
    internal nint WindowPosition;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Msg
{
    internal nint Window;
    internal uint Message;
    internal nint WParam;
    internal nint LParam;
    internal uint Time;
    internal NativePoint Point;
    internal uint Private;
}