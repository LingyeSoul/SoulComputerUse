using System;

namespace SoulComputerUse.Mcp;

/// <summary>
/// A request that cannot be served at the JSON-RPC layer. Tool failures are not signalled this way:
/// they travel back as a tool result with <c>isError</c>, so a model can read the contract error code.
/// </summary>
internal sealed class McpRequestException : Exception
{
    /// <summary>Creates one JSON-RPC level refusal.</summary>
    internal McpRequestException(int code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>The JSON-RPC 2.0 error code.</summary>
    internal int Code { get; }
}
