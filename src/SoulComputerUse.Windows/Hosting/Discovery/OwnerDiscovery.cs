namespace SoulComputerUse.Windows.Hosting.Discovery;

/// <summary>
/// The pure decision table of section 3: a CLI or MCP adapter first attaches to an existing owner,
/// starts a session only when the lock is free, asks for a retry while a live owner is still starting,
/// and otherwise fails with <c>E_LOCK_HELD</c>.
/// </summary>
/// <remarks>
/// The pipe is authoritative whenever it answers. During shutdown an owner may still answer briefly
/// after its lock object is gone, so a lock-free machine with an answering pipe attaches instead of
/// starting a second session.
/// <para>
/// A lock without an answer is ambiguous: the owner may be alive in its logon window, with the lock
/// taken and the pipe not serving yet. The per-user record resolves that case when it names a live
/// process whose start time matches, which makes the refusal retryable <c>E_NOT_READY</c> rather than
/// <c>E_LOCK_HELD</c>. A dead or absent record keeps the strict zero-wait refusal, so a leftover lock
/// still fails immediately.
/// </para>
/// <para>
/// A lock without an answer is not proof of a leftover record. Ordinary privileges cannot enumerate
/// machine-wide handles, so this layer reports only what it observed and leaves the diagnosis to the
/// operator. The message never claims that the lock is stale and never suggests deleting the ownership
/// record, because a stale record and a foreign handle need different actions.
/// </para>
/// </remarks>
public static class OwnerDiscovery
{
    /// <summary>Chooses what the caller does from the three observations.</summary>
    /// <param name="lockExists">Whether the single-instance lock object exists, probed without creating it.</param>
    /// <param name="ownerPipeReachable">Whether a pipe server answered within the probe timeout.</param>
    /// <param name="liveOwnerStarting">
    /// Whether this account's record names a live owner process whose state is still starting or
    /// connecting; only consulted when the pipe did not answer.
    /// </param>
    /// <returns>One of the four decisions of the contract.</returns>
    public static OwnerDiscoveryDecision Decide(bool lockExists, bool ownerPipeReachable, bool liveOwnerStarting)
    {
        if (ownerPipeReachable)
        {
            return OwnerDiscoveryDecision.UseExistingOwner;
        }

        if (!lockExists)
        {
            return OwnerDiscoveryDecision.StartOwnSession;
        }

        return liveOwnerStarting
            ? OwnerDiscoveryDecision.RefuseStartInProgress
            : OwnerDiscoveryDecision.RefuseLockHeld;
    }
}
