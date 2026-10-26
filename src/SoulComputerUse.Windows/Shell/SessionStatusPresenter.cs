using System;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Shell;

/// <summary>
/// Keeps the caption status in step with the session state machine: it refuses a transition the
/// contract does not allow and hands back exactly what the caption must show, so the title bar can
/// never display a state the session machine never entered.
/// </summary>
public sealed class SessionStatusPresenter
{
    private readonly AuthTier _tier;
    private readonly GateStrength _strength;

    /// <summary>Starts a session in the starting state, which is the only legal first state.</summary>
    public SessionStatusPresenter(AuthTier tier, GateStrength strength)
    {
        // Throws for the 'none' tier and for a strength that contradicts its tier, so a caption can
        // never claim a gate the probe did not establish.
        _ = SessionStatusVisuals.Describe(SessionState.Starting, tier, strength, null);
        _tier = tier;
        _strength = strength;
        CurrentState = SessionState.Starting;
        Current = Describe(SessionState.Starting, null);
    }

    /// <summary>The state the session is in.</summary>
    public SessionState CurrentState { get; private set; }

    /// <summary>What the caption shows right now.</summary>
    public SessionStatusVisual Current { get; private set; }

    /// <summary>
    /// Moves to the next state, refusing any transition the contract does not allow, and returns the
    /// caption content for the new state.
    /// </summary>
    public SessionStatusVisual MoveTo(SessionState next, BlockedCause? blockedCause)
    {
        if (!SessionStateMachine.CanTransition(CurrentState, next))
        {
            throw new InvalidOperationException(
                $"The contract does not allow {SessionStateNames.ToWireName(CurrentState)} -> {SessionStateNames.ToWireName(next)}; the caption refuses to show a state the session machine never entered.");
        }

        Current = Describe(next, blockedCause);
        CurrentState = next;
        return Current;
    }

    private SessionStatusVisual Describe(SessionState state, BlockedCause? blockedCause)
    {
        return SessionStatusVisuals.Describe(state, _tier, _strength, blockedCause);
    }
}
