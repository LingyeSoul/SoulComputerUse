namespace SoulComputerUse.Windows.Rdp.SessionLaunch;

/// <summary>
/// The raw values one hosted launch is asked for, before anything is validated or written.
/// </summary>
/// <remarks>
/// <para>
/// The launcher receives these values, not a ready plan, so that building and validating the plan is the
/// first step of <see cref="HostSessionLauncher.StartAsync"/>: an unusable id, nonce, size or directory
/// is refused before a property is written and before the machine is touched.
/// </para>
/// <para>
/// The record is plain data and holds no default: every value is supplied at the call site, and
/// <see cref="CreatePlan"/> is the only place that turns them into a validated plan.
/// </para>
/// </remarks>
public sealed record SessionLaunchPlanInput(
    string SessionId,
    string Nonce,
    string ApplicationDirectory,
    int DesktopWidth,
    int DesktopHeight,
    bool ConnectToChildSession,
    string SessionLabel)
{
    /// <summary>Builds and validates the plan for this launch.</summary>
    /// <exception cref="System.ArgumentException">
    /// A value cannot describe a usable session. The message names the field and the required shape,
    /// never the value.
    /// </exception>
    /// <exception cref="System.ArgumentOutOfRangeException">
    /// A desktop side is not positive or exceeds the allowed maximum.
    /// </exception>
    public SessionLaunchPlan CreatePlan()
    {
        return new SessionLaunchPlan(
            SessionId,
            Nonce,
            ApplicationDirectory,
            DesktopWidth,
            DesktopHeight,
            ConnectToChildSession,
            SessionLabel);
    }
}
