namespace SoulComputerUse.Mcp;

/// <summary>The JSON-RPC 2.0 error codes this adapter emits.</summary>
internal static class JsonRpcErrorCodes
{
    /// <summary>The payload is not valid JSON.</summary>
    internal const int ParseError = -32700;

    /// <summary>The envelope is not a valid JSON-RPC 2.0 request.</summary>
    internal const int InvalidRequest = -32600;

    /// <summary>The method is not part of this server's surface.</summary>
    internal const int MethodNotFound = -32601;

    /// <summary>The parameters of a known method are malformed.</summary>
    internal const int InvalidParams = -32602;

    /// <summary>The request failed for a reason outside the session contract.</summary>
    internal const int InternalError = -32603;
}
