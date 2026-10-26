using System;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.SessionAgent;

/// <summary>What the agent's claim gate decided from the ownership record.</summary>
public enum AgentClaimVerdict
{
    /// <summary>The record names this WTS session, so the agent may serve it.</summary>
    Claimed = 0,

    /// <summary>The record exists but the host has not written the child WTS id yet.</summary>
    PendingHostClaim = 1,

    /// <summary>The record names another session, another account or an ended session.</summary>
    WrongSession = 2,

    /// <summary>No ownership record exists, so this agent was not launched by an owner.</summary>
    NoOwnerRecord = 3,
}

/// <summary>
/// The pure part of the agent's claim gate. It binds the agent to the owner record's session id and
/// to the Windows session the host reserved for it. A record whose child session id is still zero is
/// accepted only provisionally: the pipe's nonce claim must arrive within the unclaimed-gate timeout.
/// </summary>
public static class AgentClaimPolicy
{
    /// <summary>Evaluates one ownership record against the agent's session and expected session id.</summary>
    /// <exception cref="ArgumentNullException">The expected session id is null.</exception>
    /// <exception cref="ArgumentException">The expected session id is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The current WTS session id is not positive.</exception>
    public static AgentClaimVerdict Evaluate(
        SessionOwnerRecord? record,
        string expectedSessionId,
        int currentSessionId)
    {
        ArgumentNullException.ThrowIfNull(expectedSessionId);
        if (string.IsNullOrWhiteSpace(expectedSessionId))
        {
            throw new ArgumentException("The expected session id must not be empty.", nameof(expectedSessionId));
        }

        if (currentSessionId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentSessionId),
                currentSessionId,
                "A child session id must be positive.");
        }

        if (record is null)
        {
            return AgentClaimVerdict.NoOwnerRecord;
        }

        if (!string.Equals(record.SessionId, expectedSessionId, StringComparison.Ordinal))
        {
            return AgentClaimVerdict.WrongSession;
        }

        if (record.State is SessionState.Closing or SessionState.Disconnected)
        {
            return AgentClaimVerdict.WrongSession;
        }

        if (record.SubagentWtsSessionId < 0)
        {
            return AgentClaimVerdict.WrongSession;
        }

        if (record.SubagentWtsSessionId == 0)
        {
            return AgentClaimVerdict.PendingHostClaim;
        }

        return record.SubagentWtsSessionId == currentSessionId
            ? AgentClaimVerdict.Claimed
            : AgentClaimVerdict.WrongSession;
    }
}