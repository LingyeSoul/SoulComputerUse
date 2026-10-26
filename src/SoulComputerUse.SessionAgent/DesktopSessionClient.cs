using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Desktop;

namespace SoulComputerUse.SessionAgent;

/// <summary>
/// The agent's view of its own desktop exposed through the shared session client surface. The state
/// is <c>ready</c> because the agent only starts serving after the shell bootstrap succeeded.
/// </summary>
public sealed class DesktopSessionClient : ISessionClient
{
    private readonly IDesktopSurface _surface;
    private readonly string _sessionId;
    private readonly AuthTier _authTier;
    private readonly GateStrength _gateStrength;

    /// <summary>Creates one client over the child session's desktop surface.</summary>
    /// <exception cref="ArgumentNullException">The surface is null.</exception>
    /// <exception cref="ArgumentException">The session id is empty or the authentication tier is none.</exception>
    public DesktopSessionClient(
        IDesktopSurface surface,
        string sessionId,
        AuthTier authTier,
        GateStrength gateStrength)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("The session id must not be empty.", nameof(sessionId));
        }

        if (authTier == AuthTier.None)
        {
            throw new ArgumentException("The 'none' authentication tier must never start an agent.", nameof(authTier));
        }

        AuthTierRules.EnsureConsistent(authTier, gateStrength);
        _surface = surface;
        _sessionId = sessionId;
        _authTier = authTier;
        _gateStrength = gateStrength;
    }

    /// <inheritdoc />
    public Task<SessionStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DesktopScreen screen = _surface.GetScreen();
        SessionStatus status = new(
            SessionStateNames.ToWireName(SessionState.Ready),
            _sessionId,
            _authTier,
            _gateStrength,
            new SessionScreen(screen.Width, screen.Height),
            null);
        status.Validate();
        return Task.FromResult(status);
    }

    /// <inheritdoc />
    public Task<byte[]> ScreenshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_surface.CapturePng());
    }

    /// <inheritdoc />
    public Task MouseMoveAsync(int x, int y, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _surface.MovePointer(x, y);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MouseClickAsync(int x, int y, MouseButton button, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _surface.Click(x, y, button, count);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MouseScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _surface.Scroll(x, y, deltaX, deltaY);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task KeyTypeAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _surface.TypeText(text);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task KeyPressAsync(IReadOnlyList<SessionKey> keys, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _surface.PressKeys(keys);
        return Task.CompletedTask;
    }
}