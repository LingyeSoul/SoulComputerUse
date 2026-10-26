using System;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>
/// Turns the machine-wide child-session switch back off and confirms the new state.
/// </summary>
/// <remarks>
/// The switch is a machine-wide capability that may have belonged to the operator or another program.
/// Only a session that recorded <c>childSessionsOpenedByUs</c> may call this, and the write alone is not
/// proof: a refused change or a switch that remains on must fail, because the caller would otherwise
/// report a restored machine while a capability it enabled is still active.
/// </remarks>
public sealed class ChildSessionsSwitchRestore : IChildSessionsSwitch
{
    private readonly IChildSessionsSetting _setting;

    /// <summary>Creates the restore over the real machine switch.</summary>
    public ChildSessionsSwitchRestore()
        : this(new WindowsChildSessionsSetting())
    {
    }

    /// <summary>Creates the restore over an injected switch, used by tests.</summary>
    /// <exception cref="ArgumentNullException">The switch is null.</exception>
    public ChildSessionsSwitchRestore(IChildSessionsSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        _setting = setting;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The read-back still reports the switch as on.</exception>
    public void Restore()
    {
        _setting.SetEnabled(false);

        if (_setting.IsEnabled())
        {
            throw new InvalidOperationException(
                "WTSEnableChildSessions(false) was accepted, but reading the state back still reports child sessions as enabled, so the machine-wide switch was not restored.");
        }
    }
}
