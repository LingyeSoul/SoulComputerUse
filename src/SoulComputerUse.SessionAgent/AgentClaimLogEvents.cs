using System;

namespace SoulComputerUse.SessionAgent;

/// <summary>
/// The log events the claim gate writes. Names are a fixed enumeration because the event is one of
/// the contract's whitelisted log fields: the name carries the finding, and nothing about the record
/// travels with it.
/// </summary>
/// <remarks>
/// "The owner record has not appeared yet" and "a record exists but does not bind this agent" are
/// separate events. Collapsing both into one made a slow launch and a genuine mismatch look identical
/// in a log, which is exactly the case an operator has to tell apart.
/// </remarks>
public static class AgentClaimLogEvents
{
    /// <summary>No record exists yet, so the agent is about to wait for the host to write one.</summary>
    public const string OwnerRecordMissingWaiting = "gate.owner_record_missing_waiting";

    /// <summary>The bounded wait for an owner record expired without a record appearing.</summary>
    public const string OwnerRecordWaitExpired = "gate.owner_record_wait_expired";

    /// <summary>The record binds this agent to its child session.</summary>
    public const string Claimed = "gate.claimed";

    /// <summary>The record exists but the host has not written the child WTS session id yet.</summary>
    public const string HostClaimPending = "gate.host_claim_pending";

    private const string WrongSessionIdDiffers = "gate.wrong_session_id_differs";
    private const string WrongSessionRecordEnded = "gate.wrong_session_record_ended";
    private const string WrongSessionInvalidChildSession = "gate.wrong_session_invalid_child_session";
    private const string WrongSessionChildSessionDiffers = "gate.wrong_session_child_session_differs";

    /// <summary>
    /// Returns the event that names a refusal reason, so a log says which finding ended the agent
    /// instead of only that some claim did not hold. Only reasons that describe an existing record are
    /// accepted: a missing record is the wait's own event, not a claim that failed.
    /// </summary>
    /// <param name="reason">The reason a claim-gate decision is a refusal.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The reason is not a refusal over an existing record, so no refusal event describes it.
    /// </exception>
    public static string ForRefusal(AgentClaimReason reason)
    {
        return reason switch
        {
            AgentClaimReason.SessionIdDiffers => WrongSessionIdDiffers,
            AgentClaimReason.RecordSessionEnded => WrongSessionRecordEnded,
            AgentClaimReason.InvalidChildSessionId => WrongSessionInvalidChildSession,
            AgentClaimReason.ChildSessionDiffers => WrongSessionChildSessionDiffers,
            _ => throw new ArgumentOutOfRangeException(
                nameof(reason),
                reason,
                "The reason does not refuse an existing record, so it has no refusal event."),
        };
    }
}