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
/// is <c>ready</c> when the desktop can receive input and <c>blocked</c> when the input desktop is
/// locked, protected or non-interactive; the agent only starts serving after the shell bootstrap
/// succeeded.
/// </summary>
public sealed class DesktopSessionClient : ISessionClient
{
    private readonly IDesktopSurface _surface;
    private readonly string _sessionId;
    private readonly AuthTier _authTier;
    private readonly GateStrength _gateStrength;
    private readonly string? _helloHint;
    private readonly Func<DesktopAvailabilityVerdict> _availability;

    /// <summary>Creates one client over the child session's desktop surface.</summary>
    /// <param name="surface">The desktop surface this child session serves.</param>
    /// <param name="sessionId">The session UUID in <c>D</c> format.</param>
    /// <param name="authTier">The tier the agent detected for itself; the refusal tier is rejected.</param>
    /// <param name="gateStrength">The strength the contract pins to <paramref name="authTier"/>.</param>
    /// <param name="helloHint">
    /// The operator hint the detection produced, or null when it produced none. Only the
    /// system-credential tier reports the hint, and its only defined value is the enumeration of
    /// section 5.1.
    /// </param>
    /// <param name="availability">
    /// Probes the child session's desktop for the status call. A blocked verdict is reported as the
    /// contract's <c>blocked</c> state while screenshots stay available; it never reaches the input
    /// path, which keeps its own guard.
    /// </param>
    /// <exception cref="ArgumentNullException">The surface or the availability probe is null.</exception>
    /// <exception cref="ArgumentException">The session id is empty, the tier is none, or the hint does not belong to the tier.</exception>
    public DesktopSessionClient(
        IDesktopSurface surface,
        string sessionId,
        AuthTier authTier,
        GateStrength gateStrength,
        string? helloHint,
        Func<DesktopAvailabilityVerdict> availability)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(availability);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("The session id must not be empty.", nameof(sessionId));
        }

        if (authTier == AuthTier.None)
        {
            throw new ArgumentException("The 'none' authentication tier must never start an agent.", nameof(authTier));
        }

        AuthTierRules.EnsureConsistent(authTier, gateStrength);
        if (helloHint is not null
            && (authTier != AuthTier.SystemCredential
                || !string.Equals(helloHint, AgentAuthTier.HelloNotEnrolledHint, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"The operator hint belongs to the '{AuthTierNames.ToWireName(AuthTier.SystemCredential)}' tier only, and '{AgentAuthTier.HelloNotEnrolledHint}' is its only defined value.",
                nameof(helloHint));
        }

        _surface = surface;
        _sessionId = sessionId;
        _authTier = authTier;
        _gateStrength = gateStrength;
        _helloHint = helloHint;
        _availability = availability;
    }

    /// <inheritdoc />
    public Task<SessionStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DesktopScreen screen = _surface.GetScreen();
        DesktopAvailabilityVerdict verdict = _availability();
        SessionState state = verdict == DesktopAvailabilityVerdict.Usable
            ? SessionState.Ready
            : SessionState.Blocked;
        SessionStatus status = new(
            SessionStateNames.ToWireName(state),
            _sessionId,
            _authTier,
            _gateStrength,
            new SessionScreen(screen.Width, screen.Height),
            _helloHint);
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