namespace SoulComputerUse.Windows.Hosting.Discovery;

/// <summary>
/// The pure decision table of section 3: a CLI or MCP adapter first attaches to an existing owner,
/// starts a session only when the lock is free, and otherwise fails with <c>E_LOCK_HELD</c>.
/// </summary>
/// <remarks>
/// The pipe is authoritative whenever it answers. During shutdown an owner may still answer briefly
/// after its lock object is gone, so a lock-free machine with an answering pipe attaches instead of
/// starting a second session. The opposite window, a lock without an answer, cannot be attached to at
/// all; the contract's zero-wait rule makes that a <c>E_LOCK_HELD</c> refusal even when the owner is
/// merely still starting.
/// <para>
/// A lock without an answer is not proof of a leftover record. Ordinary privileges cannot enumerate
/// machine-wide handles, so this layer reports only what it observed and leaves the diagnosis to the
/// operator. The message never claims that the lock is stale and never suggests deleting the ownership
/// record, because a stale record and a foreign handle need different actions.
/// </para>
/// </remarks>
public static class OwnerDiscovery
{
    /// <summary>Chooses what the caller does from the two observations.</summary>
    /// <param name="lockExists">Whether the single-instance lock object exists, probed without creating it.</param>
    /// <param name="ownerPipeReachable">Whether a pipe server answered within the probe timeout.</param>
    /// <returns>One of the three decisions of the contract.</returns>
    public static OwnerDiscoveryDecision Decide(bool lockExists, bool ownerPipeReachable)
    {
        if (ownerPipeReachable)
        {
            return OwnerDiscoveryDecision.UseExistingOwner;
        }

        return lockExists
            ? OwnerDiscoveryDecision.RefuseLockHeld
            : OwnerDiscoveryDecision.StartOwnSession;
    }
}
