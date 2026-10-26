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
/// Why the claim gate reached its verdict. The verdict decides the exit code; the reason decides the
/// gate's log event, so "the owner record has not appeared yet" and "a record exists but does not
/// bind this agent" never collapse into one indistinguishable line.
/// </summary>
public enum AgentClaimReason
{
    /// <summary>The record binds this agent to its child session.</summary>
    Bound = 0,

    /// <summary>The host has not written the child WTS session id yet.</summary>
    HostClaimPending = 1,

    /// <summary>No ownership record exists.</summary>
    OwnerRecordAbsent = 2,

    /// <summary>The record names a different session id.</summary>
    SessionIdDiffers = 3,

    /// <summary>The record is in a terminal state, so its session already ended.</summary>
    RecordSessionEnded = 4,

    /// <summary>The record carries a child WTS session id that cannot name a session.</summary>
    InvalidChildSessionId = 5,

    /// <summary>The record names a child WTS session other than this process's session.</summary>
    ChildSessionDiffers = 6,
}

/// <summary>One claim-gate answer: the verdict, and the reason behind it for logging.</summary>
public sealed record AgentClaimDecision(AgentClaimVerdict Verdict, AgentClaimReason Reason);

/// <summary>
/// The pure part of the agent's claim gate. It binds the agent to the owner record's session id and
/// to the Windows session the host reserved for it. A record whose child session id is still zero is
/// accepted only provisionally: the pipe's nonce claim must arrive within the unclaimed-gate timeout.
/// </summary>
/// <remarks>
/// <para>
/// The claim gate is decided from the ownership record plus the pipe's nonce claim, never from the
/// command line. The command line only carries the session id the host also wrote into the record,
/// and the binding value in the record is the child WTS session id the host observed for this process.
/// </para>
/// <para>
/// <c>SubagentWtsSessionId == 0</c> means the host has not written the child session id yet, which is
/// a normal stage of the launch sequence rather than "this agent is unclaimed". A zero therefore
/// continues to the pipe's nonce handshake, and the unclaimed gate is what finally bounds it.
/// </para>
/// </remarks>
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
        return Decide(record, expectedSessionId, currentSessionId).Verdict;
    }

    /// <summary>
    /// Evaluates one ownership record and reports both the verdict and the reason behind it.
    /// </summary>
    /// <param name="record">The ownership record, or null when none exists.</param>
    /// <param name="expectedSessionId">The session id from the agent's command line.</param>
    /// <param name="currentSessionId">This process's Windows Terminal Services session id.</param>
    /// <exception cref="ArgumentNullException">The expected session id is null.</exception>
    /// <exception cref="ArgumentException">The expected session id is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The current WTS session id is not positive.</exception>
    public static AgentClaimDecision Decide(
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
            return new AgentClaimDecision(AgentClaimVerdict.NoOwnerRecord, AgentClaimReason.OwnerRecordAbsent);
        }

        if (!string.Equals(record.SessionId, expectedSessionId, StringComparison.Ordinal))
        {
            return Wrong(AgentClaimReason.SessionIdDiffers);
        }

        if (record.State is SessionState.Closing or SessionState.Disconnected)
        {
            // A record in a terminal state describes a session that already ended. Serving it would
            // hand this agent to a host that is no longer there, so the gate refuses at once.
            return Wrong(AgentClaimReason.RecordSessionEnded);
        }

        if (record.SubagentWtsSessionId < 0)
        {
            return Wrong(AgentClaimReason.InvalidChildSessionId);
        }

        if (record.SubagentWtsSessionId == 0)
        {
            return new AgentClaimDecision(AgentClaimVerdict.PendingHostClaim, AgentClaimReason.HostClaimPending);
        }

        return record.SubagentWtsSessionId == currentSessionId
            ? new AgentClaimDecision(AgentClaimVerdict.Claimed, AgentClaimReason.Bound)
            : Wrong(AgentClaimReason.ChildSessionDiffers);
    }

    private static AgentClaimDecision Wrong(AgentClaimReason reason)
    {
        return new AgentClaimDecision(AgentClaimVerdict.WrongSession, reason);
    }
}