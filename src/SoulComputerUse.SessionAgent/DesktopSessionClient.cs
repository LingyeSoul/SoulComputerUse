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
/// is <c>ready</c> when the desktop can receive input, <c>blocked</c> when the input desktop is
/// locked, protected or non-interactive, and <c>disconnected</c> when the session has no attached
/// client; the agent only starts serving after the shell bootstrap succeeded.
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
    /// contract's <c>blocked</c> state, and a session whose client is gone as <c>disconnected</c>;
    /// screenshots stay available while a locked or protected desktop still has a surface to read.
    /// The probe never reaches the input path, which keeps its own guard.
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
        SessionState state = verdict switch
        {
            DesktopAvailabilityVerdict.Usable => SessionState.Ready,
            DesktopAvailabilityVerdict.Disconnected => SessionState.Disconnected,
            _ => SessionState.Blocked,
        };
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
    /// <remarks>
    /// A session with no attached client and a non-interactive session have no surface to read, so
    /// they are refused with their contract code before the surface is touched. A locked or
    /// protected desktop is still captured, because section 1.3 keeps the frame available there; a
    /// capture that then fails is reported as blocked rather than as an agent defect.
    /// </remarks>
    /// <exception cref="SessionOperationException">
    /// The session is disconnected or non-interactive, or a blocked desktop could not be captured.
    /// </exception>
    public Task<byte[]> ScreenshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DesktopAvailabilityVerdict verdict = _availability();
        if (verdict is DesktopAvailabilityVerdict.Disconnected or DesktopAvailabilityVerdict.BlockedNonInteractive)
        {
            throw Refuse(verdict);
        }

        try
        {
            return Task.FromResult(_surface.CapturePng());
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            // The desktop can change between the probe and the capture. A blocked or disconnected
            // verdict explains the failure; a usable verdict leaves a real agent defect, which the
            // contract reports with an enumerated reason instead of the exception text.
            DesktopAvailabilityVerdict afterFailure = _availability();
            if (afterFailure != DesktopAvailabilityVerdict.Usable)
            {
                throw Refuse(afterFailure);
            }

            throw new SessionOperationException(
                SessionErrorCode.AgentError,
                "window-gone",
                true);
        }
    }

    private static SessionOperationException Refuse(DesktopAvailabilityVerdict verdict)
    {
        return new SessionOperationException(
            DesktopBlockReason.ToErrorCode(verdict),
            DesktopBlockReason.Describe(verdict),
            false);
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