using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Ipc;

/// <summary>The pipe names the product uses. The two names belong to two different hops and never mix.</summary>
public static class IpcPipeNames
{
    /// <summary>
    /// The CLI and MCP server to owner hop. This is the pipe this namespace's client and server use.
    /// </summary>
    /// <remarks>
    /// The contract's <c>SoulComputerUse.agent.{sessionId}</c> pipe is the host to child-session agent
    /// hop and is a different transport with its own handshake and peer checks. Never use this name
    /// for that hop and never use a session id here.
    /// </remarks>
    public const string Owner = "SoulComputerUse.owner";
}

/// <summary>
/// The owner-side entry point of the owner pipe: the session executes one tool call and returns either
/// a result or a contract error. Implementations own the session rules; the pipe only carries the call.
/// </summary>
public interface ISessionToolHandler
{
    /// <summary>
    /// Executes one tool call with the arguments exactly as the client sent them. The handler must
    /// validate them itself and return a failure instead of throwing for a bad argument.
    /// </summary>
    Task<SessionToolResult> HandleAsync(SessionTool tool, JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>
/// One tool call outcome: either a result payload or a contract error object, never both.
/// </summary>
public sealed record SessionToolResult
{
    private SessionToolResult(bool succeeded, JsonElement? result, SessionError? error)
    {
        Succeeded = succeeded;
        Result = result;
        Error = error;
    }

    /// <summary>True when the call produced a result payload.</summary>
    public bool Succeeded { get; }

    /// <summary>The result payload of a successful call; null on failure.</summary>
    public JsonElement? Result { get; }

    /// <summary>The contract error object of a failed call; null on success.</summary>
    public SessionError? Error { get; }

    /// <summary>Creates a successful result around one already-encoded payload.</summary>
    public static SessionToolResult Success(JsonElement result)
    {
        return new SessionToolResult(true, result, null);
    }

    /// <summary>Creates a failed result around one contract error object.</summary>
    public static SessionToolResult Failure(SessionError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new SessionToolResult(false, null, error);
    }
}
