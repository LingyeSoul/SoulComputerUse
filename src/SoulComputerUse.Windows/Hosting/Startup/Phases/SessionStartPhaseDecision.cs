using System;

namespace SoulComputerUse.Windows.Hosting.Startup.Phases;

/// <summary>
/// One immutable phase decision: the phase the facts support, the action that comes next, and the fixed
/// explanation for both.
/// </summary>
/// <remarks>
/// The constructor refuses a combination that would hide a missing step, so a decision that exists
/// always either names an action or says the session is ready. The explanation is a fixed template from
/// <see cref="SessionStartPhaseMessages"/> and never carries a path, an account, a SID or a nonce.
/// </remarks>
public sealed record SessionStartPhaseDecision
{
    /// <summary>Builds one decision and refuses contradictions.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The phase or the action is not a defined value.</exception>
    /// <exception cref="ArgumentException">
    /// The ready decision names an action, a non-ready decision names none, or the explanation is empty.
    /// </exception>
    public SessionStartPhaseDecision(SessionStartPhase phase, SessionStartPhaseAction? nextAction, string reason)
    {
        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown session start phase.");
        }

        if (nextAction is SessionStartPhaseAction action && !Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(nextAction), action, "Unknown session start phase action.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        bool isReady = phase == SessionStartPhase.Ready;
        if (isReady == nextAction.HasValue)
        {
            throw new ArgumentException(
                isReady
                    ? "A ready session has no action left, so a ready decision must not name one."
                    : "A session that is not ready has an action left, so the decision must name it.",
                nameof(nextAction));
        }

        Phase = phase;
        NextAction = nextAction;
        Reason = reason;
    }

    /// <summary>The phase the collected facts support.</summary>
    public SessionStartPhase Phase { get; }

    /// <summary>The action that comes next, or null when the session is ready.</summary>
    public SessionStartPhaseAction? NextAction { get; }

    /// <summary>The fixed explanation for the phase and the action.</summary>
    public string Reason { get; }

    /// <summary>Whether no action is left.</summary>
    public bool IsReady => Phase == SessionStartPhase.Ready;
}
