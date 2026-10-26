using System;

namespace SoulComputerUse.Core.Session;

/// <summary>
/// The session state machine. Every transition the contract does not list as legal is refused, so
/// a caller can never move the session into a state the rest of the system does not expect.
/// </summary>
public static class SessionStateMachine
{
    /// <summary>Tells whether the contract allows moving from one state to another.</summary>
    public static bool CanTransition(SessionState from, SessionState to)
    {
        return from switch
        {
            SessionState.Starting => to is SessionState.AwaitingCredentials
                or SessionState.Connecting
                or SessionState.Disconnected
                or SessionState.Closing,
            SessionState.AwaitingCredentials => to is SessionState.Connecting
                or SessionState.Disconnected
                or SessionState.Closing,
            SessionState.Connecting => to is SessionState.Ready
                or SessionState.Disconnected
                or SessionState.Blocked
                or SessionState.Closing,
            SessionState.Ready => to is SessionState.Disconnected
                or SessionState.Blocked
                or SessionState.Closing,
            SessionState.Disconnected => to is SessionState.Closing,
            SessionState.Blocked => to is SessionState.Ready
                or SessionState.Disconnected
                or SessionState.Closing,
            SessionState.Closing => false,
            _ => throw new ArgumentOutOfRangeException(nameof(from), from, "Unknown session state."),
        };
    }
}