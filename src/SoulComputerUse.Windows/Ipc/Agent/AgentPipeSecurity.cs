using System;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SoulComputerUse.Windows.Ipc.Agent;

/// <summary>
/// The explicit DACL of section 4 of the interface contract. Only the current user and LocalSystem
/// receive <see cref="PipeAccessRights.FullControl"/>; no group, no anonymous identity, and no
/// inherited rule is present.
/// </summary>
public static class AgentPipeSecurity
{
    /// <summary>Builds the DACL applied to every agent pipe instance.</summary>
    /// <exception cref="InvalidOperationException">The current process token has no user SID.</exception>
    public static PipeSecurity Create()
    {
        SecurityIdentifier currentUser = AgentPeerIdentity.CurrentUserSid();
        SecurityIdentifier localSystem = new(WellKnownSidType.LocalSystemSid, null);

        PipeSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(localSystem, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
}