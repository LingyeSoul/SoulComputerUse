using System;
using SoulComputerUse.Core.Protocol;

namespace SoulComputerUse.Windows.Desktop;

/// <summary>
/// Builds native input events from contract-level desktop actions. Every function is pure: it reads
/// its arguments and returns a new event without touching the desktop.
/// </summary>
public static class DesktopInputPlanner
{
    /// <summary>The largest value of the normalized absolute mouse range.</summary>
    public const int NormalizedMaximum = 65535;

    /// <summary>
    /// Maps one native pixel coordinate onto the inclusive 0..65535 absolute mouse range.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The extent is not positive, or the coordinate lies outside the origin and extent.
    /// </exception>
    public static int NormalizeCoordinate(int coordinate, int origin, int extent)
    {
        if (extent <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(extent), extent, "The coordinate extent must be positive.");
        }

        long upperBound = (long)origin + extent;
        if (coordinate < origin || coordinate >= upperBound)
        {
            throw new ArgumentOutOfRangeException(
                nameof(coordinate),
                coordinate,
                $"The coordinate must satisfy {origin} <= coordinate < {upperBound}.");
        }

        if (extent == 1)
        {
            return 0;
        }

        long relative = (long)coordinate - origin;
        return (int)((relative * NormalizedMaximum) / (extent - 1));
    }

    /// <summary>Builds an absolute move to one native pixel coordinate.</summary>
    /// <exception cref="ArgumentNullException">The screen is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The coordinate lies outside the screen.</exception>
    public static INPUT CreateMove(DesktopScreen screen, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(screen);

        int normalizedX = NormalizeCoordinate(x, screen.X, screen.Width);
        int normalizedY = NormalizeCoordinate(y, screen.Y, screen.Height);
        uint flags = DesktopInputFlags.MouseEventFMove
            | DesktopInputFlags.MouseEventFAbsolute
            | DesktopInputFlags.MouseEventFVirtualDesk;
        return CreateMouse(flags, normalizedX, normalizedY, 0);
    }

    /// <summary>Builds a primary button press.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The button is not a contract mouse button.</exception>
    public static INPUT CreateButtonDown(MouseButton button)
    {
        return CreateMouse(ButtonDownFlag(button), 0, 0, 0);
    }

    /// <summary>Builds a primary button release.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The button is not a contract mouse button.</exception>
    public static INPUT CreateButtonUp(MouseButton button)
    {
        return CreateMouse(ButtonUpFlag(button), 0, 0, 0);
    }

    /// <summary>Builds one vertical wheel event with the native wheel delta.</summary>
    public static INPUT CreateVerticalWheel(int deltaY)
    {
        return CreateMouse(DesktopInputFlags.MouseEventFWheel, 0, 0, unchecked((uint)deltaY));
    }

    /// <summary>Builds one horizontal wheel event with the native wheel delta.</summary>
    public static INPUT CreateHorizontalWheel(int deltaX)
    {
        return CreateMouse(DesktopInputFlags.MouseEventFHWheel, 0, 0, unchecked((uint)deltaX));
    }

    /// <summary>Builds a key press for one contract key.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The key is not a contract key.</exception>
    public static INPUT CreateKeyDown(SessionKey key)
    {
        return CreateKeyboard(VirtualKeyCode(key), 0, 0);
    }

    /// <summary>Builds a key release for one contract key.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The key is not a contract key.</exception>
    public static INPUT CreateKeyUp(SessionKey key)
    {
        return CreateKeyboard(VirtualKeyCode(key), 0, DesktopInputFlags.KeyEventFKeyUp);
    }

    /// <summary>Builds a Unicode press for one UTF-16 code unit.</summary>
    public static INPUT CreateUnicodeDown(char codeUnit)
    {
        return CreateKeyboard(0, codeUnit, DesktopInputFlags.KeyEventFUnicode);
    }

    /// <summary>Builds a Unicode release for one UTF-16 code unit.</summary>
    public static INPUT CreateUnicodeUp(char codeUnit)
    {
        return CreateKeyboard(0, codeUnit, DesktopInputFlags.KeyEventFUnicode | DesktopInputFlags.KeyEventFKeyUp);
    }

    /// <summary>Returns the virtual key code of one contract key.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The key is not a contract key.</exception>
    public static ushort VirtualKeyCode(SessionKey key)
    {
        return key switch
        {
            SessionKey.Enter => 0x0D,
            SessionKey.Esc => 0x1B,
            SessionKey.Tab => 0x09,
            SessionKey.Delete => 0x2E,
            SessionKey.Backspace => 0x08,
            SessionKey.Home => 0x24,
            SessionKey.End => 0x23,
            SessionKey.Up => 0x26,
            SessionKey.Down => 0x28,
            SessionKey.Left => 0x25,
            SessionKey.Right => 0x27,
            SessionKey.Win => 0x5B,
            SessionKey.Ctrl => 0x11,
            SessionKey.Alt => 0x12,
            SessionKey.Shift => 0x10,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown session key."),
        };
    }

    private static uint ButtonDownFlag(MouseButton button)
    {
        return button switch
        {
            MouseButton.Left => DesktopInputFlags.MouseEventFLeftDown,
            MouseButton.Right => DesktopInputFlags.MouseEventFRightDown,
            MouseButton.Middle => DesktopInputFlags.MouseEventFMiddleDown,
            _ => throw new ArgumentOutOfRangeException(nameof(button), button, "Unknown mouse button."),
        };
    }

    private static uint ButtonUpFlag(MouseButton button)
    {
        return button switch
        {
            MouseButton.Left => DesktopInputFlags.MouseEventFLeftUp,
            MouseButton.Right => DesktopInputFlags.MouseEventFRightUp,
            MouseButton.Middle => DesktopInputFlags.MouseEventFMiddleUp,
            _ => throw new ArgumentOutOfRangeException(nameof(button), button, "Unknown mouse button."),
        };
    }

    private static INPUT CreateMouse(uint flags, int dx, int dy, uint mouseData)
    {
        return new INPUT
        {
            Type = DesktopInputFlags.InputMouse,
            Data = new InputUnion
            {
                Mouse = new MOUSEINPUT
                {
                    Dx = dx,
                    Dy = dy,
                    MouseData = mouseData,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = 0,
                },
            },
        };
    }

    private static INPUT CreateKeyboard(ushort virtualKey, ushort scanCode, uint flags)
    {
        return new INPUT
        {
            Type = DesktopInputFlags.InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KEYBDINPUT
                {
                    VirtualKey = virtualKey,
                    ScanCode = scanCode,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = 0,
                },
            },
        };
    }
}