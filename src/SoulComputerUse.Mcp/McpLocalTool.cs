using SoulComputerUse.Core.Protocol;

namespace SoulComputerUse.Mcp;

/// <summary>
/// A tool the MCP adapter answers itself, without an IPC method behind it. These names are not part
/// of the owner/agent pipe's method set and never enter <see cref="SessionTool"/>.
/// </summary>
internal enum McpLocalTool
{
    /// <summary>Opens the product window on a new session; the operator logs on there.</summary>
    StartSession = 0,
}
