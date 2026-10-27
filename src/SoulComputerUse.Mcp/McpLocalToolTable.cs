using System;
using System.Collections.Generic;
using SoulComputerUse.Core.Protocol;

namespace SoulComputerUse.Mcp;

/// <summary>
/// The MCP-facing description of every adapter-local tool: the name the model calls, a one-line
/// description, and the JSON Schema of its arguments. It mirrors
/// <see cref="SessionToolCatalog"/> and stays separate from it, so a local process
/// action never drifts into the IPC method surface.
/// </summary>
internal static class McpLocalToolTable
{
    private const string EmptySchema = """
        {"type":"object","properties":{},"additionalProperties":false,"required":[]}
        """;

    private static readonly IReadOnlyList<McpLocalTool> AllTools =
    [
        McpLocalTool.StartSession,
    ];

    /// <summary>Every local tool, in the order <c>tools/list</c> appends them.</summary>
    internal static IReadOnlyList<McpLocalTool> Tools => AllTools;

    /// <summary>Returns the wire name of one local tool.</summary>
    internal static string NameOf(McpLocalTool tool)
    {
        return tool switch
        {
            McpLocalTool.StartSession => "start_session",
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "Unknown local tool."),
        };
    }

    /// <summary>Returns the description shown to the model for one local tool.</summary>
    internal static string Describe(McpLocalTool tool)
    {
        return tool switch
        {
            McpLocalTool.StartSession => "Open the product window on a new session; the operator logs on themselves",
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "Unknown local tool."),
        };
    }

    /// <summary>Returns the JSON Schema of one local tool's arguments.</summary>
    internal static string SchemaOf(McpLocalTool tool)
    {
        return tool switch
        {
            McpLocalTool.StartSession => EmptySchema,
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "Unknown local tool."),
        };
    }

    /// <summary>Looks up a local tool by its wire name; an unknown name is not a local tool.</summary>
    internal static bool TryFind(string wireName, out McpLocalTool tool)
    {
        ArgumentNullException.ThrowIfNull(wireName);

        foreach (McpLocalTool candidate in AllTools)
        {
            if (string.Equals(NameOf(candidate), wireName, StringComparison.Ordinal))
            {
                tool = candidate;
                return true;
            }
        }

        tool = default;
        return false;
    }
}
