using SoulComputerUse.Windows.ChildSessions;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>The real machine-wide child-session switch of this machine.</summary>
public sealed class WindowsChildSessionsSetting : IChildSessionsSetting
{
    /// <inheritdoc />
    public bool IsEnabled()
    {
        return ChildSessionsProbe.IsEnabled();
    }

    /// <inheritdoc />
    public void SetEnabled(bool enabled)
    {
        ChildSessionsProbe.SetEnabled(enabled);
    }
}
