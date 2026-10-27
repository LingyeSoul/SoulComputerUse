using System.Security.Principal;

namespace SoulComputerUse.Windows.Hosting.Launch;

/// <summary>
/// Reads the caller's own token and reports whether it is in the built-in Administrators role.
/// </summary>
/// <remarks>
/// The check answers from the token's groups, so an administrator account running with a filtered,
/// non-elevated token reports <see langword="false"/> and its start is allowed at the operator's normal
/// integrity. Only a token whose administrator authority is actually granted reports
/// <see langword="true"/>.
/// </remarks>
public sealed class ElevationGuard : IElevationGuard
{
    /// <inheritdoc />
    public bool IsCallerElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
