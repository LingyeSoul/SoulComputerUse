using System;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SoulComputerUse.Windows.Ipc.Security;

/// <summary>
/// The explicit DACL of the owner pipe. Only the current user and LocalSystem receive
/// <see cref="PipeAccessRights.FullControl"/>; no group, no anonymous identity, and no inherited rule
/// is present.
/// </summary>
/// <remarks>
/// The DACL admits every process of the current account, not only this product's owner process. The
/// per-connection peer check in <see cref="OwnerPipePeer"/> confirms the same limit; neither check
/// can tell two processes of one account apart.
/// </remarks>
public static class OwnerPipeSecurity
{
    /// <summary>Builds the DACL applied to every owner pipe instance.</summary>
    /// <exception cref="InvalidOperationException">The current process token has no user SID.</exception>
    public static PipeSecurity Create()
    {
        SecurityIdentifier currentUser = OwnerPipePeer.CurrentUserSid();
        SecurityIdentifier localSystem = new(WellKnownSidType.LocalSystemSid, null);

        PipeSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(localSystem, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
