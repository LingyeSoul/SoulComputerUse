using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Core.Protocol;

/// <summary>
/// A tool call refused by the session owner. The code is the contract's machine-readable error code;
/// the message is already free of machine-specific data.
/// </summary>
public sealed class SessionOperationException : Exception
{
    /// <summary>Creates the exception for one refused or failed tool call.</summary>
    public SessionOperationException(SessionErrorCode code, string message, bool retryable)
        : base(message)
    {
        Code = code;
        Retryable = retryable;
    }

    /// <summary>The contract error code.</summary>
    public SessionErrorCode Code { get; }

    /// <summary>True when retrying the same call may succeed.</summary>
    public bool Retryable { get; }
}

/// <summary>
/// The one client surface the CLI and the MCP adapter share. Every coordinate is in the remote
/// session's native pixels, with the origin at the top-left corner.
/// </summary>
public interface ISessionClient
{
    /// <summary>Returns the current session state.</summary>
    Task<SessionStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Returns one PNG frame of the remote desktop.</summary>
    Task<byte[]> ScreenshotAsync(CancellationToken cancellationToken);

    /// <summary>Moves the pointer.</summary>
    Task MouseMoveAsync(int x, int y, CancellationToken cancellationToken);

    /// <summary>Clicks a mouse button.</summary>
    Task MouseClickAsync(int x, int y, MouseButton button, int count, CancellationToken cancellationToken);

    /// <summary>Scrolls the wheel.</summary>
    Task MouseScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken cancellationToken);

    /// <summary>Types text verbatim.</summary>
    Task KeyTypeAsync(string text, CancellationToken cancellationToken);

    /// <summary>Presses a key combination.</summary>
    Task KeyPressAsync(IReadOnlyList<SessionKey> keys, CancellationToken cancellationToken);
}

/// <summary>
/// A client that has no transport yet. It fails every call with the contract's disconnected code
/// instead of pretending the session is usable.
/// </summary>
public sealed class UnavailableSessionClient : ISessionClient
{
    /// <inheritdoc />
    public Task<SessionStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        throw Disconnected();
    }

    /// <inheritdoc />
    public Task<byte[]> ScreenshotAsync(CancellationToken cancellationToken)
    {
        throw Disconnected();
    }

    /// <inheritdoc />
    public Task MouseMoveAsync(int x, int y, CancellationToken cancellationToken)
    {
        throw Disconnected();
    }

    /// <inheritdoc />
    public Task MouseClickAsync(int x, int y, MouseButton button, int count, CancellationToken cancellationToken)
    {
        throw Disconnected();
    }

    /// <inheritdoc />
    public Task MouseScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken cancellationToken)
    {
        throw Disconnected();
    }

    /// <inheritdoc />
    public Task KeyTypeAsync(string text, CancellationToken cancellationToken)
    {
        throw Disconnected();
    }

    /// <inheritdoc />
    public Task KeyPressAsync(IReadOnlyList<SessionKey> keys, CancellationToken cancellationToken)
    {
        throw Disconnected();
    }

    private static SessionOperationException Disconnected()
    {
        return new SessionOperationException(
            SessionErrorCode.Disconnected,
            "No session owner is reachable, so the call was not sent. Start a session and retry.",
            false);
    }
}
