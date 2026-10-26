namespace SoulComputerUse.Windows.Hosting.Discovery;

/// <summary>
/// What a CLI or MCP adapter does after probing the machine-wide lock and the owner pipe.
/// </summary>
public enum OwnerDiscoveryDecision
{
    /// <summary>A session owner answered on the pipe, so the caller attaches to it as a client.</summary>
    UseExistingOwner = 0,

    /// <summary>No lock object exists and no owner answered, so the caller may start a session and hold the lock.</summary>
    StartOwnSession = 1,

    /// <summary>The lock object exists but no owner answered, so the caller must fail with <c>E_LOCK_HELD</c>.</summary>
    RefuseLockHeld = 2,
}
