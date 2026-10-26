using System;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>Moves this session into the contract's <c>closing</c> state before anything destructive runs.</summary>
/// <remarks>
/// The production implementation belongs to the owner service, which writes the new state to the
/// ownership record; this layer only requires that entering the state happens first.
/// </remarks>
public interface ISessionStateTransition
{
    /// <summary>Moves the session to <c>closing</c>.</summary>
    /// <exception cref="Exception">The state machine refused the transition or the record could not be written.</exception>
    void EnterClosing();
}
