using System;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.SingleInstance;

/// <summary>What to do with leftovers from a previous session.</summary>
public enum RecoveryDecision
{
    /// <summary>No record for this account, so this is a normal start.</summary>
    NormalStart = 0,

    /// <summary>The record's owner is gone, so the leftovers may be cleaned up.</summary>
    RecoverLeftovers = 1,

    /// <summary>The record cannot be trusted enough to clean up, so a human has to look.</summary>
    ManualInterventionRequired = 2,
}

/// <summary>What the recovery rules are allowed to know about the record and its owner process.</summary>
public sealed record RecoveryEvidence(
    bool RecordExists,
    bool OwnerProcessAlive,
    bool OwnerProcessStartTimeMatches,
    SessionState RecordedState,
    DateTimeOffset AcquiredAt,
    DateTimeOffset Now);

/// <summary>
/// The abandoned-lock recovery rules from the interface contract.
/// </summary>
/// <remarks>
/// These rules deliberately do not depend on how the lock was acquired. Because the lock object has
/// no owner thread and dies with its last handle, a crashed owner leaves no abandonment event behind:
/// the next process simply creates the object. Deciding from the ownership record on every
/// successful acquisition is therefore the only shape that actually runs in production.
/// </remarks>
public static class SessionRecoveryPolicy
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    /// <summary>Applies the contract's four criteria to one record and its owner process.</summary>
    public static RecoveryDecision Evaluate(RecoveryEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (!evidence.RecordExists)
        {
            return RecoveryDecision.NormalStart;
        }

        bool ownerGone = !evidence.OwnerProcessAlive || !evidence.OwnerProcessStartTimeMatches;
        bool stateIsTerminal = evidence.RecordedState is SessionState.Disconnected or SessionState.Closing;
        bool recordIsStale = evidence.Now - evidence.AcquiredAt > StaleAfter;

        return ownerGone && (stateIsTerminal || recordIsStale)
            ? RecoveryDecision.RecoverLeftovers
            : RecoveryDecision.ManualInterventionRequired;
    }
}