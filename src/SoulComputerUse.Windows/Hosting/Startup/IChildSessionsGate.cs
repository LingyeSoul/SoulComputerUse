using System;
using SoulComputerUse.Windows.ChildSessions;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>
/// The machine's child-session state as a port: probe whether the switch is on, read each policy value
/// that gates a connection, and ask for elevation to turn the switch on.
/// </summary>
/// <remarks>
/// The policy values are read one at a time on purpose. A value stored with the wrong type has to be
/// reported under the code that owns that value, and a combined read could not tell the caller which
/// value failed. Probing and enabling are separate calls because enabling needs the operator's consent
/// through the elevation helper, so the pipeline must be able to decide between them from the probe
/// alone.
/// </remarks>
public interface IChildSessionsGate
{
    /// <summary>Reports whether the machine-wide child-session switch is on.</summary>
    /// <exception cref="Exception">The state could not be read.</exception>
    bool IsEnabled();

    /// <summary>Reads one policy value; a missing value is <see cref="PolicyState.Missing"/>.</summary>
    /// <exception cref="Exception">
    /// The value exists but is not a DWORD, so the machine has not expressed consent that could be read.
    /// </exception>
    PolicyState ReadPolicy(PolicyValue value);

    /// <summary>
    /// Asks the operator for administrator consent and enables the switch through the elevation helper.
    /// The helper path is derived by the implementation from the application's own directory; this port
    /// never accepts a path from a caller.
    /// </summary>
    /// <exception cref="Exception">The helper is missing, or the prompt could not finish.</exception>
    ElevationResult Enable();
}
