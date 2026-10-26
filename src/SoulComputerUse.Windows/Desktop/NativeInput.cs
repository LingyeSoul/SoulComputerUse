using System.Runtime.InteropServices;

namespace SoulComputerUse.Windows.Desktop;

/// <summary>The native constants of the INPUT structures, from winuser.h.</summary>
public static class DesktopInputFlags
{
    /// <summary>The INPUT type of a mouse event.</summary>
    public const uint InputMouse = 0;

    /// <summary>The INPUT type of a keyboard event.</summary>
    public const uint InputKeyboard = 1;

    /// <summary>The MOUSEEVENTF_MOVE flag.</summary>
    public const uint MouseEventFMove = 0x0001;

    /// <summary>The MOUSEEVENTF_LEFTDOWN flag.</summary>
    public const uint MouseEventFLeftDown = 0x0002;

    /// <summary>The MOUSEEVENTF_LEFTUP flag.</summary>
    public const uint MouseEventFLeftUp = 0x0004;

    /// <summary>The MOUSEEVENTF_RIGHTDOWN flag.</summary>
    public const uint MouseEventFRightDown = 0x0008;

    /// <summary>The MOUSEEVENTF_RIGHTUP flag.</summary>
    public const uint MouseEventFRightUp = 0x0010;

    /// <summary>The MOUSEEVENTF_MIDDLEDOWN flag.</summary>
    public const uint MouseEventFMiddleDown = 0x0020;

    /// <summary>The MOUSEEVENTF_MIDDLEUP flag.</summary>
    public const uint MouseEventFMiddleUp = 0x0040;

    /// <summary>The MOUSEEVENTF_HWHEEL flag.</summary>
    public const uint MouseEventFHWheel = 0x0100;

    /// <summary>The MOUSEEVENTF_WHEEL flag.</summary>
    public const uint MouseEventFWheel = 0x0800;

    /// <summary>The MOUSEEVENTF_VIRTUALDESK flag.</summary>
    public const uint MouseEventFVirtualDesk = 0x4000;

    /// <summary>The MOUSEEVENTF_ABSOLUTE flag.</summary>
    public const uint MouseEventFAbsolute = 0x8000;

    /// <summary>The KEYEVENTF_KEYUP flag.</summary>
    public const uint KeyEventFKeyUp = 0x0002;

    /// <summary>The KEYEVENTF_UNICODE flag.</summary>
    public const uint KeyEventFUnicode = 0x0004;
}

/// <summary>The native INPUT structure. Its x64 size is 40 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct INPUT
{
    /// <summary>The input type, either <see cref="DesktopInputFlags.InputMouse"/> or <see cref="DesktopInputFlags.InputKeyboard"/>.</summary>
    public uint Type;

    /// <summary>The type-specific payload.</summary>
    public InputUnion Data;
}

/// <summary>The native union inside INPUT. Every member shares offset zero.</summary>
[StructLayout(LayoutKind.Explicit)]
public struct InputUnion
{
    /// <summary>The mouse payload.</summary>
    [FieldOffset(0)]
    public MOUSEINPUT Mouse;

    /// <summary>The keyboard payload.</summary>
    [FieldOffset(0)]
    public KEYBDINPUT Keyboard;

    /// <summary>The hardware payload.</summary>
    [FieldOffset(0)]
    public HARDWAREINPUT Hardware;
}

/// <summary>The native MOUSEINPUT structure.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MOUSEINPUT
{
    /// <summary>The absolute x coordinate or the relative horizontal movement.</summary>
    public int Dx;

    /// <summary>The absolute y coordinate or the relative vertical movement.</summary>
    public int Dy;

    /// <summary>The wheel delta or the extra button data.</summary>
    public uint MouseData;

    /// <summary>The mouse event flags.</summary>
    public uint Flags;

    /// <summary>The event time in milliseconds, or zero for the system to supply it.</summary>
    public uint Time;

    /// <summary>The extra value associated with the event.</summary>
    public nint ExtraInfo;
}

/// <summary>The native KEYBDINPUT structure.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT
{
    /// <summary>The virtual key code.</summary>
    public ushort VirtualKey;

    /// <summary>The scan code, or the UTF-16 code unit for a Unicode event.</summary>
    public ushort ScanCode;

    /// <summary>The keyboard event flags.</summary>
    public uint Flags;

    /// <summary>The event time in milliseconds, or zero for the system to supply it.</summary>
    public uint Time;

    /// <summary>The extra value associated with the event.</summary>
    public nint ExtraInfo;
}

/// <summary>The native HARDWAREINPUT structure.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct HARDWAREINPUT
{
    /// <summary>The hardware message.</summary>
    public uint Message;

    /// <summary>The low word of the message parameter.</summary>
    public ushort ParameterLow;

    /// <summary>The high word of the message parameter.</summary>
    public ushort ParameterHigh;
}