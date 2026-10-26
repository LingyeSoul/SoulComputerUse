using System;
using System.Collections.Generic;

namespace SoulComputerUse.Core.Protocol;

/// <summary>
/// The MCP-facing description of every session tool: the name the model calls, a one-line
/// description, and the JSON Schema of its arguments. The schemas are the interface contract's
/// section 5 schemas, verbatim, so an adapter cannot drift from the published surface.
/// </summary>
public static class SessionToolCatalog
{
    private static readonly IReadOnlyList<SessionTool> AllTools =
    [
        SessionTool.GetStatus,
        SessionTool.Screenshot,
        SessionTool.MouseMove,
        SessionTool.MouseClick,
        SessionTool.MouseScroll,
        SessionTool.KeyType,
        SessionTool.KeyPress,
    ];

    /// <summary>Every tool, in the contract's order.</summary>
    public static IReadOnlyList<SessionTool> Tools => AllTools;

    /// <summary>Returns the JSON Schema of one tool's arguments.</summary>
    public static string SchemaOf(SessionTool tool)
    {
        return tool switch
        {
            SessionTool.GetStatus => EmptySchema,
            SessionTool.Screenshot => EmptySchema,
            SessionTool.MouseMove => MouseMoveSchema,
            SessionTool.MouseClick => MouseClickSchema,
            SessionTool.MouseScroll => MouseScrollSchema,
            SessionTool.KeyType => KeyTypeSchema,
            SessionTool.KeyPress => KeyPressSchema,
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "Unknown session tool."),
        };
    }

    /// <summary>Returns the description shown to the model for one tool.</summary>
    public static string Describe(SessionTool tool)
    {
        return tool switch
        {
            SessionTool.GetStatus => "Return the session state, the authentication tier and the remote screen size.",
            SessionTool.Screenshot => "Capture the remote desktop and return one PNG image.",
            SessionTool.MouseMove => "Move the pointer to a remote pixel coordinate.",
            SessionTool.MouseClick => "Click a mouse button at a remote pixel coordinate.",
            SessionTool.MouseScroll => "Scroll the wheel at a remote pixel coordinate.",
            SessionTool.KeyType => "Type text verbatim into the remote session; the text is never logged.",
            SessionTool.KeyPress => "Press a key combination, up to five keys held together.",
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "Unknown session tool."),
        };
    }

    /// <summary>True when the tool returns an image instead of a JSON object.</summary>
    public static bool ReturnsImage(SessionTool tool)
    {
        return tool == SessionTool.Screenshot;
    }

    private const string EmptySchema = """
        {"type":"object","properties":{},"additionalProperties":false,"required":[]}
        """;

    private const string MouseMoveSchema = """
        {"type":"object","properties":{"x":{"type":"integer","minimum":0},"y":{"type":"integer","minimum":0}},"additionalProperties":false,"required":["x","y"]}
        """;

    private const string MouseClickSchema = """
        {"type":"object","properties":{"x":{"type":"integer","minimum":0},"y":{"type":"integer","minimum":0},"button":{"type":"string","enum":["left","right","middle"]},"count":{"type":"integer","minimum":1,"maximum":3}},"additionalProperties":false,"required":["x","y","button","count"]}
        """;

    private const string MouseScrollSchema = """
        {"type":"object","properties":{"x":{"type":"integer","minimum":0},"y":{"type":"integer","minimum":0},"deltaX":{"type":"integer"},"deltaY":{"type":"integer"}},"additionalProperties":false,"required":["x","y","deltaX","deltaY"]}
        """;

    private const string KeyTypeSchema = """
        {"type":"object","properties":{"text":{"type":"string","maxLength":1000}},"additionalProperties":false,"required":["text"]}
        """;

    private const string KeyPressSchema = """
        {"type":"object","properties":{"keys":{"type":"array","items":{"type":"string","enum":["enter","esc","tab","delete","backspace","home","end","up","down","left","right","win","ctrl","alt","shift"]},"minItems":1,"maxItems":5}},"additionalProperties":false,"required":["keys"]}
        """;
}
