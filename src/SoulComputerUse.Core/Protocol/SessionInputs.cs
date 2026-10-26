using System;

namespace SoulComputerUse.Core.Protocol;

/// <summary>The mouse buttons the contract accepts.</summary>
public enum MouseButton
{
    /// <summary>The primary button.</summary>
    Left = 0,

    /// <summary>The secondary button.</summary>
    Right = 1,

    /// <summary>The middle button.</summary>
    Middle = 2,
}

/// <summary>The wire names of <see cref="MouseButton"/>.</summary>
public static class MouseButtonNames
{
    /// <summary>Returns the contract name of a mouse button.</summary>
    public static string ToWireName(MouseButton button)
    {
        return button switch
        {
            MouseButton.Left => "left",
            MouseButton.Right => "right",
            MouseButton.Middle => "middle",
            _ => throw new ArgumentOutOfRangeException(nameof(button), button, "Unknown mouse button."),
        };
    }

    /// <summary>Parses a contract mouse button name.</summary>
    public static MouseButton FromWireName(string wireName)
    {
        ArgumentNullException.ThrowIfNull(wireName);
        return wireName switch
        {
            "left" => MouseButton.Left,
            "right" => MouseButton.Right,
            "middle" => MouseButton.Middle,
            _ => throw new ArgumentOutOfRangeException(nameof(wireName), wireName, "Unknown mouse button name."),
        };
    }
}

/// <summary>The keys the contract accepts in a key combination.</summary>
public enum SessionKey
{
    /// <summary>Enter.</summary>
    Enter = 0,

    /// <summary>Escape.</summary>
    Esc = 1,

    /// <summary>Tab.</summary>
    Tab = 2,

    /// <summary>Delete.</summary>
    Delete = 3,

    /// <summary>Backspace.</summary>
    Backspace = 4,

    /// <summary>Home.</summary>
    Home = 5,

    /// <summary>End.</summary>
    End = 6,

    /// <summary>Arrow up.</summary>
    Up = 7,

    /// <summary>Arrow down.</summary>
    Down = 8,

    /// <summary>Arrow left.</summary>
    Left = 9,

    /// <summary>Arrow right.</summary>
    Right = 10,

    /// <summary>The Windows key.</summary>
    Win = 11,

    /// <summary>Control.</summary>
    Ctrl = 12,

    /// <summary>Alt.</summary>
    Alt = 13,

    /// <summary>Shift.</summary>
    Shift = 14,
}

/// <summary>The wire names of <see cref="SessionKey"/>.</summary>
public static class SessionKeyNames
{
    /// <summary>Returns the contract name of a key.</summary>
    public static string ToWireName(SessionKey key)
    {
        return key switch
        {
            SessionKey.Enter => "enter",
            SessionKey.Esc => "esc",
            SessionKey.Tab => "tab",
            SessionKey.Delete => "delete",
            SessionKey.Backspace => "backspace",
            SessionKey.Home => "home",
            SessionKey.End => "end",
            SessionKey.Up => "up",
            SessionKey.Down => "down",
            SessionKey.Left => "left",
            SessionKey.Right => "right",
            SessionKey.Win => "win",
            SessionKey.Ctrl => "ctrl",
            SessionKey.Alt => "alt",
            SessionKey.Shift => "shift",
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown session key."),
        };
    }

    /// <summary>Parses a contract key name.</summary>
    public static SessionKey FromWireName(string wireName)
    {
        ArgumentNullException.ThrowIfNull(wireName);
        return wireName switch
        {
            "enter" => SessionKey.Enter,
            "esc" => SessionKey.Esc,
            "tab" => SessionKey.Tab,
            "delete" => SessionKey.Delete,
            "backspace" => SessionKey.Backspace,
            "home" => SessionKey.Home,
            "end" => SessionKey.End,
            "up" => SessionKey.Up,
            "down" => SessionKey.Down,
            "left" => SessionKey.Left,
            "right" => SessionKey.Right,
            "win" => SessionKey.Win,
            "ctrl" => SessionKey.Ctrl,
            "alt" => SessionKey.Alt,
            "shift" => SessionKey.Shift,
            _ => throw new ArgumentOutOfRangeException(nameof(wireName), wireName, "Unknown session key name."),
        };
    }
}
