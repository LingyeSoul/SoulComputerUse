using System;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.Windows.Desktop;

/// <summary>
/// Maps a desktop verdict onto the blocked cause the state machine and the caption report, and onto
/// the fixed message a refused tool call carries.
/// </summary>
/// <remarks>
/// The three verdicts get three distinct messages so an operator can tell a locked desktop from a
/// UAC prompt, but only two causes exist: the caption treats a locked desktop and a protected
/// desktop the same way, because both are resolved by the operator at the machine.
/// </remarks>
public static class DesktopBlockReason
{
    /// <summary>Returns the cause the blocked state reports for one verdict.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The verdict is <see cref="DesktopAvailabilityVerdict.Usable"/>,
    /// <see cref="DesktopAvailabilityVerdict.Disconnected"/> or not defined; none of them can be a
    /// blocked cause.
    /// </exception>
    public static BlockedCause ToBlockedCause(DesktopAvailabilityVerdict verdict)
    {
        return verdict switch
        {
            DesktopAvailabilityVerdict.BlockedLockedDesktop => BlockedCause.ProtectedDesktop,
            DesktopAvailabilityVerdict.BlockedSecureDesktop => BlockedCause.ProtectedDesktop,
            DesktopAvailabilityVerdict.BlockedNonInteractive => BlockedCause.NonInteractiveSession,
            _ => throw new ArgumentOutOfRangeException(
                nameof(verdict),
                verdict,
                "Only a blocked verdict has a cause; a usable or disconnected desktop and an unknown value do not."),
        };
    }

    /// <summary>
    /// Returns the contract error code one refused verdict reports. The disconnected verdict is the
    /// contract's terminal state, not a blocked one, because nothing can restore a client that is
    /// gone.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The verdict is <see cref="DesktopAvailabilityVerdict.Usable"/> or not defined.
    /// </exception>
    public static SessionErrorCode ToErrorCode(DesktopAvailabilityVerdict verdict)
    {
        return verdict switch
        {
            DesktopAvailabilityVerdict.Disconnected => SessionErrorCode.Disconnected,
            DesktopAvailabilityVerdict.BlockedLockedDesktop => SessionErrorCode.Blocked,
            DesktopAvailabilityVerdict.BlockedSecureDesktop => SessionErrorCode.Blocked,
            DesktopAvailabilityVerdict.BlockedNonInteractive => SessionErrorCode.Blocked,
            _ => throw new ArgumentOutOfRangeException(
                nameof(verdict),
                verdict,
                "Only a refused verdict has an error code; a usable desktop and an unknown value do not."),
        };
    }

    /// <summary>
    /// Returns the fixed message for one blocked verdict. The text names the condition and the way
    /// out only: it carries no window title, account, machine name or path, so it stays safe in an
    /// error payload and in a log.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The verdict is <see cref="DesktopAvailabilityVerdict.Usable"/> or not defined.
    /// </exception>
    public static string Describe(DesktopAvailabilityVerdict verdict)
    {
        return verdict switch
        {
            DesktopAvailabilityVerdict.BlockedLockedDesktop =>
                "The desktop is locked, so the tool call was refused. Unlock the session's desktop and retry.",
            DesktopAvailabilityVerdict.BlockedSecureDesktop =>
                "A protected desktop such as a UAC prompt is showing, so the tool call was refused. Complete the prompt on the session's own desktop and retry.",
            DesktopAvailabilityVerdict.BlockedNonInteractive =>
                "The session is not interactive, so the tool call was refused. Connect to the session and retry.",
            DesktopAvailabilityVerdict.Disconnected =>
                "The session has no attached client, so the tool call was refused. Start a new session; this one does not reconnect automatically.",
            _ => throw new ArgumentOutOfRangeException(
                nameof(verdict),
                verdict,
                "Only a refused verdict has a message; a usable desktop and an unknown value do not."),
        };
    }
}

/// <summary>
/// Refuses work while the calling session's desktop cannot receive it. Every call probes again: the
/// desktop can be locked between two calls in the same session, so a cached verdict would let input
/// through onto a protected desktop.
/// </summary>
/// <remarks>
/// The guard never writes a log entry of its own. The blocked facts reach the structured log through
/// the tool call that the guard refuses, which keeps this type usable in a path that must not log.
/// </remarks>
public sealed class BlockedGuard
{
    private readonly Func<DesktopBlockObservation> _probe;

    /// <summary>Creates a guard that probes the real desktop through Win32.</summary>
    public BlockedGuard()
        : this(DesktopBlockProbe.Probe)
    {
    }

    /// <summary>Creates a guard over a supplied probe, so a test can present a blocked desktop.</summary>
    /// <exception cref="ArgumentNullException">The probe is null.</exception>
    public BlockedGuard(Func<DesktopBlockObservation> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
    }

    /// <summary>
    /// Probes the desktop once and returns its current availability. The caller decides whether the
    /// verdict blocks an action or is reported through <c>get_status</c>.
    /// </summary>
    public DesktopAvailabilityVerdict Observe()
    {
        DesktopBlockObservation observation = _probe();
        return DesktopAvailability.Evaluate(
            observation.InputDesktopOpenSucceeded,
            observation.SessionConnectState,
            observation.InputDesktopName,
            observation.SessionWindowStation);
    }

    /// <summary>
    /// Probes the desktop and throws when it cannot accept a tool call.
    /// </summary>
    /// <exception cref="SessionOperationException">
    /// The desktop is locked, protected or non-interactive (<see cref="SessionErrorCode.Blocked"/>),
    /// or the session has no attached client (<see cref="SessionErrorCode.Disconnected"/>). Neither is
    /// retryable until the operator acts.
    /// </exception>
    public void EnsureUsable()
    {
        DesktopAvailabilityVerdict verdict = Observe();

        if (verdict == DesktopAvailabilityVerdict.Usable)
        {
            return;
        }

        throw new SessionOperationException(
            DesktopBlockReason.ToErrorCode(verdict),
            DesktopBlockReason.Describe(verdict),
            false);
    }
}
