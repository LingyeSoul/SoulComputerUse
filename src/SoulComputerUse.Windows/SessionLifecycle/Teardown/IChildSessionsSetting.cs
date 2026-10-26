using System;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>
/// Reads and changes the machine-wide child-session switch.
/// </summary>
/// <remarks>
/// The seam exists so the restore adapter can be exercised without changing a machine-wide capability
/// that needs administrator rights. The adapter only ever passes <c>false</c>: turning the capability on
/// belongs to the startup path, which needs the operator's consent.
/// </remarks>
public interface IChildSessionsSetting
{
    /// <summary>Reports whether the machine currently allows child sessions.</summary>
    /// <exception cref="Exception">The state cannot be read.</exception>
    bool IsEnabled();

    /// <summary>Turns the machine-wide capability on or off.</summary>
    /// <param name="enabled">The requested state.</param>
    /// <exception cref="Exception">The system refused the change.</exception>
    void SetEnabled(bool enabled);
}
