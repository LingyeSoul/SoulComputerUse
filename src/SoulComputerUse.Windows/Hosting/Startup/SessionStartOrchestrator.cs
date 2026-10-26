using System;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.ChildSessions;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>
/// The startup decision table of sections 3, 3.1.1, 3.3 and 7.2, split into one function per gate plus
/// the composition that carries out a whole start.
/// </summary>
/// <remarks>
/// Splitting the table by gate lets the runner refuse at the exact stage that found the problem, while
/// <see cref="Decide"/> remains the single authority for the whole sequence and for the next step. Both
/// entry points use the same functions, so a rule can never be stated twice.
/// <para>
/// The gate order is the contract's pipeline order, and each gate is evaluated only when everything
/// before it allows the start:
/// </para>
/// <list type="number">
/// <item>The lock: another process holding it refuses first, because this process owns no handle.</item>
/// <item>The ownership record: one that cannot be taken over refuses and is never deleted.</item>
/// <item>The credential tier: <c>none</c> refuses without a fallback or a downgrade.</item>
/// <item>The leftover sweep: a failed sweep refuses, because a leftover answers a connection without a logon.</item>
/// <item>The child-session policy: a denied value refuses before elevation is asked for.</item>
/// <item>Elevation: a failure is a refusal; the request to elevate is a continuation, not one.</item>
/// <item>The agent: its own contract code is passed through unchanged.</item>
/// </list>
/// <para>
/// No explanation carries a path, an account, a SID or a nonce: each is a fixed template plus an
/// enumeration, a count or the contract's policy value names.
/// </para>
/// </remarks>
public static class SessionStartOrchestrator
{
    /// <summary>Decides one whole start from a complete facts snapshot.</summary>
    /// <returns>
    /// <see cref="SessionStartKind.RecoverAndProceed"/> while a leftover record still has to be deleted,
    /// and <see cref="SessionStartKind.Proceed"/> once the takeover has been carried out.
    /// </returns>
    /// <exception cref="ArgumentNullException">The facts are null.</exception>
    /// <exception cref="ArgumentException">
    /// A refused child-session decision names no policy value, a finished sweep has no child-session
    /// decision, or a failed agent attach names no code.
    /// </exception>
    public static SessionStartPlan Decide(SessionStartFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        SessionStartKind kind = facts.RecordStatus == OwnerRecordStatus.TakeOver
            ? SessionStartKind.RecoverAndProceed
            : SessionStartKind.Proceed;
        string continueReason = kind == SessionStartKind.RecoverAndProceed
            ? SessionStartMessages.RecoverAndProceed
            : SessionStartMessages.Proceed;

        SessionStartRefusal? lockRefusal = EvaluateLock(facts.LockOutcome);
        if (lockRefusal is not null)
        {
            return Refused(lockRefusal);
        }

        SessionStartRefusal? recordRefusal = EvaluateRecord(facts.RecordStatus);
        if (recordRefusal is not null)
        {
            return Refused(recordRefusal);
        }

        if (facts.AuthTier is null)
        {
            // The credential gate is probed after the record is written and before the sweep, so an
            // unprobed tier means the prologue has not run yet.
            return Continue(kind, SessionStartStep.Sweep, continueReason);
        }

        SessionStartRefusal? tierRefusal = EvaluateAuthTier(facts.AuthTier.Value);
        if (tierRefusal is not null)
        {
            return Refused(tierRefusal);
        }

        if (facts.Sweep is null)
        {
            return Continue(kind, SessionStartStep.Sweep, continueReason);
        }

        SessionStartRefusal? sweepRefusal = EvaluateSweep(facts.Sweep);
        if (sweepRefusal is not null)
        {
            return Refused(sweepRefusal);
        }

        ChildSessionsDecision childSessions = facts.ChildSessions
            ?? throw new ArgumentException(
                "A finished sweep must be followed by the machine's child-session decision.",
                nameof(facts));

        SessionStartRefusal? childSessionsRefusal = EvaluateChildSessions(childSessions);
        if (childSessionsRefusal is not null)
        {
            return Refused(childSessionsRefusal);
        }

        if (facts.ElevationFailure is ElevationFailure elevationFailure)
        {
            return Refused(EvaluateElevation(elevationFailure));
        }

        if (childSessions.Kind == ChildSessionsDecisionKind.RequestElevation)
        {
            return Continue(kind, SessionStartStep.EnableChildSessions, SessionStartMessages.ElevationRequired);
        }

        if (facts.Agent is null)
        {
            return Continue(kind, SessionStartStep.ConnectAgent, continueReason);
        }

        AgentAttachOutcome agent = facts.Agent;
        if (!agent.Connected)
        {
            return Refused(EvaluateAgent(agent));
        }

        // No takeover reaches this point: the leftover record is deleted before the sweep, so the only
        // start that can run a session is an ordinary one.
        return Continue(SessionStartKind.Proceed, SessionStartStep.Run, continueReason);
    }

    /// <summary>Evaluates the single-instance lock gate.</summary>
    /// <returns>The refusal, or null when this process holds the lock.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The outcome is not a defined value.</exception>
    public static SessionStartRefusal? EvaluateLock(LockAcquireOutcome outcome)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown lock acquisition outcome.");
        }

        return outcome == LockAcquireOutcome.HeldElsewhere
            ? new SessionStartRefusal(SessionErrorCode.LockHeld, SessionStartMessages.LockHeld)
            : null;
    }

    /// <summary>Evaluates the ownership-record gate.</summary>
    /// <returns>The refusal, or null when no record exists or the record may be taken over.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The status is not a defined value.</exception>
    public static SessionStartRefusal? EvaluateRecord(OwnerRecordStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown ownership record status.");
        }

        return status == OwnerRecordStatus.NotTakeable
            ? new SessionStartRefusal(SessionErrorCode.LockStaleRecord, SessionStartMessages.RecordNotTakeable)
            : null;
    }

    /// <summary>Evaluates the credential gate tier, with no fallback for the refused tier.</summary>
    /// <returns>The refusal, or null when the tier can carry a session.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The tier is not a defined value.</exception>
    public static SessionStartRefusal? EvaluateAuthTier(Core.Session.AuthTier tier)
    {
        if (!Enum.IsDefined(tier))
        {
            throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown authentication tier.");
        }

        return tier == Core.Session.AuthTier.None
            ? new SessionStartRefusal(SessionErrorCode.ManualIntervention, SessionStartMessages.AuthTierNone)
            : null;
    }

    /// <summary>Evaluates the pre-connection sweep.</summary>
    /// <returns>The refusal, or null when the sweep proved that no leftover is logged on.</returns>
    /// <exception cref="ArgumentNullException">The outcome is null.</exception>
    public static SessionStartRefusal? EvaluateSweep(LeftoverSweepOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (outcome.Succeeded)
        {
            return null;
        }

        int loggedOff = outcome.Report?.LoggedOffSessionIds.Count ?? 0;
        int remaining = outcome.Report?.RemainingSessionIds.Count ?? 0;
        return new SessionStartRefusal(
            SessionErrorCode.ManualIntervention,
            SessionStartMessages.SweepFailed(loggedOff, remaining));
    }

    /// <summary>Evaluates the machine's child-session decision, including both policy values.</summary>
    /// <returns>The refusal, or null when child sessions are allowed or need elevation.</returns>
    /// <exception cref="ArgumentNullException">The decision is null.</exception>
    /// <exception cref="ArgumentException">A refused decision names no policy value.</exception>
    public static SessionStartRefusal? EvaluateChildSessions(ChildSessionsDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (decision.Kind != ChildSessionsDecisionKind.Refuse)
        {
            return null;
        }

        PolicyValue value = decision.Value
            ?? throw new ArgumentException(
                "A refused child-session decision must name the policy value that refused it.",
                nameof(decision));
        PolicyProblem problem = decision.Problem
            ?? throw new ArgumentException(
                "A refused child-session decision must say why the policy value refused the connection.",
                nameof(decision));

        string valueName = value switch
        {
            PolicyValue.RdpInbound => ChildSessionsPolicy.RdpInboundValueName,
            PolicyValue.ChildConnections => ChildSessionsPolicy.ChildConnectionsValueName,
            _ => throw new ArgumentOutOfRangeException(nameof(decision), value, "Unknown policy value."),
        };
        SessionErrorCode code = value switch
        {
            PolicyValue.RdpInbound => SessionErrorCode.RdpInboundDenied,
            PolicyValue.ChildConnections => SessionErrorCode.ChildConnectionsDenied,
            _ => throw new ArgumentOutOfRangeException(nameof(decision), value, "Unknown policy value."),
        };

        // The policy layer's own explanation names the value and says what makes it allow a connection,
        // which is exactly what the contract requires for both a denied and a missing value.
        return new SessionStartRefusal(code, ChildSessionsPolicy.DescribeProblem(valueName, problem));
    }

    /// <summary>Evaluates a failed elevation attempt.</summary>
    /// <returns>The refusal that belongs to the enumerated failure.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The failure is not a defined value.</exception>
    public static SessionStartRefusal EvaluateElevation(ElevationFailure failure)
    {
        return failure switch
        {
            ElevationFailure.Declined => new SessionStartRefusal(
                SessionErrorCode.ChildSessionsDeclined,
                SessionStartMessages.ChildSessionsDeclined),
            ElevationFailure.Unavailable => new SessionStartRefusal(
                SessionErrorCode.ElevationUnavailable,
                SessionStartMessages.ElevationUnavailable),
            ElevationFailure.HelperFailed => new SessionStartRefusal(
                SessionErrorCode.ChildSessionsDisabled,
                SessionStartMessages.ChildSessionsDisabled),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown elevation failure."),
        };
    }

    /// <summary>Evaluates a failed agent attach, passing the connector's code through unchanged.</summary>
    /// <returns>The refusal that belongs to the observed code.</returns>
    /// <exception cref="ArgumentNullException">The outcome is null.</exception>
    /// <exception cref="ArgumentException">A failed outcome names no code.</exception>
    public static SessionStartRefusal EvaluateAgent(AgentAttachOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (outcome.Connected)
        {
            throw new ArgumentException("An established agent connection is not a refusal.", nameof(outcome));
        }

        Core.Session.SessionErrorCode code = outcome.FailureCode
            ?? throw new ArgumentException(
                "A failed agent attach outcome must carry the contract code the connector observed.",
                nameof(outcome));

        return new SessionStartRefusal(code, SessionStartMessages.AgentFailed(code));
    }

    private static SessionStartPlan Refused(SessionStartRefusal refusal)
    {
        return new SessionStartPlan(SessionStartKind.Refuse, refusal.Code, refusal.Reason, SessionStartStep.None);
    }

    private static SessionStartPlan Continue(SessionStartKind kind, SessionStartStep step, string reason)
    {
        return new SessionStartPlan(kind, null, reason, step);
    }
}
