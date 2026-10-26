using System;

namespace SoulComputerUse.Core.Protocol;

/// <summary>
/// The tool surface shared by the CLI and the MCP adapter. The wire names are the contract's tool
/// names and must not change without a contract revision.
/// </summary>
public enum SessionTool
{
    /// <summary>Returns the session state and the remote screen size.</summary>
    GetStatus = 0,

    /// <summary>Returns one PNG frame of the remote desktop.</summary>
    Screenshot = 1,

    /// <summary>Moves the pointer inside the remote session.</summary>
    MouseMove = 2,

    /// <summary>Clicks a mouse button at a remote coordinate.</summary>
    MouseClick = 3,

    /// <summary>Scrolls the wheel at a remote coordinate.</summary>
    MouseScroll = 4,

    /// <summary>Types text into the remote session.</summary>
    KeyType = 5,

    /// <summary>Presses a key combination in the remote session.</summary>
    KeyPress = 6,
}

/// <summary>The wire names of the session tools.</summary>
public static class SessionToolNames
{
    /// <summary>Returns the contract name of a tool.</summary>
    public static string ToWireName(SessionTool tool)
    {
        return tool switch
        {
            SessionTool.GetStatus => "get_status",
            SessionTool.Screenshot => "screenshot",
            SessionTool.MouseMove => "mouse_move",
            SessionTool.MouseClick => "mouse_click",
            SessionTool.MouseScroll => "mouse_scroll",
            SessionTool.KeyType => "key_type",
            SessionTool.KeyPress => "key_press",
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "Unknown session tool."),
        };
    }

    /// <summary>Parses a contract tool name; unknown names are refused instead of guessed.</summary>
    public static SessionTool FromWireName(string wireName)
    {
        ArgumentNullException.ThrowIfNull(wireName);
        return wireName switch
        {
            "get_status" => SessionTool.GetStatus,
            "screenshot" => SessionTool.Screenshot,
            "mouse_move" => SessionTool.MouseMove,
            "mouse_click" => SessionTool.MouseClick,
            "mouse_scroll" => SessionTool.MouseScroll,
            "key_type" => SessionTool.KeyType,
            "key_press" => SessionTool.KeyPress,
            _ => throw new ArgumentOutOfRangeException(nameof(wireName), wireName, "Unknown session tool name."),
        };
    }
}
