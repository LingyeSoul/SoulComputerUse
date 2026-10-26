using System;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Ipc.Security;

/// <summary>
/// The refusal frames the owner server sends for a connection it ends before a request can be
/// correlated with an answer, and the fixed id those frames carry.
/// </summary>
internal static class OwnerPipeRefusals
{
    /// <summary>
    /// The id every uncorrelated refusal carries. A peer is refused before any frame is read, so no
    /// request id exists; zero can never be a client request id, because the client starts at one.
    /// </summary>
    internal const int UncorrelatedId = 0;

    /// <summary>Creates the <c>E_IPC_PEER</c> refusal for a peer that failed the account check.</summary>
    /// <exception cref="ArgumentException"><paramref name="message"/> is empty or whitespace.</exception>
    internal static IpcResponse Peer(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return Create(SessionErrorCode.IpcPeer, message);
    }

    /// <summary>Creates the <c>E_IPC_PROTOCOL</c> refusal for a line over the frame limit.</summary>
    internal static IpcResponse FrameTooLong()
    {
        return Create(SessionErrorCode.IpcProtocol, OwnerPipeLimits.ExceededMessage);
    }

    private static IpcResponse Create(SessionErrorCode code, string message)
    {
        IpcError error = new(SessionErrorCodeNames.ToWireName(code), message, false, null);
        return IpcResponse.CreateFailure(UncorrelatedId, error);
    }
}
