using System;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Ipc.Agent;

/// <summary>
/// The explicit failures of the agent pipe. Every failure carries a section 2 error code through
/// <see cref="SessionOperationException"/>; no path turns a refusal into a success.
/// </summary>
internal static class AgentErrors
{
    /// <summary>The peer process or its account did not match the session.</summary>
    internal static SessionOperationException Peer(string message)
    {
        return new SessionOperationException(SessionErrorCode.IpcPeer, message, false);
    }

    /// <summary>A frame, version, or frame type is outside the agent protocol.</summary>
    internal static SessionOperationException Protocol(string message)
    {
        return new SessionOperationException(SessionErrorCode.IpcProtocol, message, false);
    }

    /// <summary>The handshake or the heartbeat did not arrive within its contract window.</summary>
    internal static SessionOperationException Timeout(string message)
    {
        return new SessionOperationException(
            SessionErrorCode.AgentTimeout,
            message,
            SessionErrorCatalog.IsRetryable(SessionErrorCode.AgentTimeout));
    }

    /// <summary>The agent process is confirmed gone.</summary>
    internal static SessionOperationException Dead(string message)
    {
        return new SessionOperationException(
            SessionErrorCode.AgentDead,
            message,
            SessionErrorCatalog.IsRetryable(SessionErrorCode.AgentDead));
    }

    /// <summary>The pipe is not connected or was lost; there is no automatic reconnect.</summary>
    internal static SessionOperationException Disconnected(string pipeName)
    {
        return new SessionOperationException(
            SessionErrorCode.Disconnected,
            "The child-session agent pipe '" + pipeName + "' is not connected, so the call was not sent.",
            SessionErrorCatalog.IsRetryable(SessionErrorCode.Disconnected));
    }

    /// <summary>
    /// Turns one wire error into the client exception. An unknown code is refused as a protocol error
    /// instead of being mapped to a nearby contract code; a known code keeps the peer's message and
    /// retryable flag unchanged.
    /// </summary>
    /// <exception cref="SessionOperationException">Always; either the peer's code or <c>E_IPC_PROTOCOL</c>.</exception>
    internal static SessionOperationException FromWireError(IpcError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!SessionIpcRules.TryResolveErrorCode(error.Code, out SessionErrorCode code))
        {
            throw Protocol(
                "The child-session agent sent an error code that is not part of the contract, so the response was refused.");
        }

        return new SessionOperationException(code, error.Message, error.Retryable);
    }
}