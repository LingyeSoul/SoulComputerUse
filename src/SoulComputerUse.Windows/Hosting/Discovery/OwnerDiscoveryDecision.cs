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

    /// <summary>
    /// The lock object exists and this account's record names a live owner that is still starting or
    /// connecting, so the caller must fail with the retryable <c>E_NOT_READY</c> instead of treating the
    /// lock as held by a stale process.
    /// </summary>
    RefuseStartInProgress = 3,
}
