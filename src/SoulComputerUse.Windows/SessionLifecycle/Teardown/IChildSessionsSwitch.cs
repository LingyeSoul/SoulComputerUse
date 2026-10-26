using System;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>Restores the machine-wide child-session switch that this session turned on.</summary>
public interface IChildSessionsSwitch
{
    /// <summary>
    /// Turns the switch off and confirms the new state. A refusal or an unconfirmed read-back must throw
    /// rather than report success.
    /// </summary>
    /// <exception cref="Exception">The switch could not be turned off or the read-back still reports it on.</exception>
    void Restore();
}
