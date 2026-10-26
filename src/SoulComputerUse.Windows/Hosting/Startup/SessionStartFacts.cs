using System;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.ChildSessions;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>
/// Everything the startup decision depends on, in the order the contract evaluates it. A fact that only
/// exists later in the pipeline is nullable, and its absence is what tells the decision which stage
/// comes next.
/// </summary>
/// <remarks>
/// The value is immutable, so one decision is a pure function of one snapshot: the same facts always
/// produce the same plan, and no caller can change a fact while a decision is being made.
/// <para>
/// The tier is nullable because the lock and the record are judged before the credential gate is probed.
/// The decision never picks a default for a missing tier: it refuses on an earlier fact if there is one,
/// and otherwise throws, so an absent probe cannot be mistaken for a favourable one.
/// </para>
/// </remarks>
public sealed record SessionStartFacts
{
    /// <summary>Creates one facts snapshot and rejects values that could hide a refusal.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An enum value is not defined.</exception>
    /// <exception cref="ArgumentException">
    /// An elevation failure is set while the machine did not ask for elevation, or an agent outcome
    /// exists without the sweep and the child-session decision that must precede it.
    /// </exception>
    public SessionStartFacts(
        LockAcquireOutcome lockOutcome,
        OwnerRecordStatus recordStatus,
        AuthTier? authTier,
        LeftoverSweepOutcome? sweep,
        ChildSessionsDecision? childSessions,
        ElevationFailure? elevationFailure,
        AgentAttachOutcome? agent)
    {
        if (!Enum.IsDefined(lockOutcome))
        {
            throw new ArgumentOutOfRangeException(nameof(lockOutcome), lockOutcome, "Unknown lock acquisition outcome.");
        }

        if (!Enum.IsDefined(recordStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(recordStatus), recordStatus, "Unknown ownership record status.");
        }

        if (authTier is AuthTier tier && !Enum.IsDefined(tier))
        {
            throw new ArgumentOutOfRangeException(nameof(authTier), tier, "Unknown authentication tier.");
        }

        if (elevationFailure is ElevationFailure failure && !Enum.IsDefined(failure))
        {
            throw new ArgumentOutOfRangeException(nameof(elevationFailure), failure, "Unknown elevation failure.");
        }

        if (elevationFailure is not null)
        {
            if (childSessions is null)
            {
                throw new ArgumentException(
                    "An elevation failure needs the child-session decision that asked for elevation.",
                    nameof(elevationFailure));
            }

            if (childSessions.Kind != ChildSessionsDecisionKind.RequestElevation)
            {
                throw new ArgumentException(
                    "An elevation failure is only meaningful when the machine asked for elevation.",
                    nameof(elevationFailure));
            }
        }

        if (agent is not null && (sweep is null || childSessions is null))
        {
            throw new ArgumentException(
                "An agent outcome can only exist after the sweep and the child-session probe have run.",
                nameof(agent));
        }

        LockOutcome = lockOutcome;
        RecordStatus = recordStatus;
        AuthTier = authTier;
        Sweep = sweep;
        ChildSessions = childSessions;
        ElevationFailure = elevationFailure;
        Agent = agent;
    }

    /// <summary>What the single-instance lock attempt reported.</summary>
    public LockAcquireOutcome LockOutcome { get; }

    /// <summary>What this account's ownership record means for a new start.</summary>
    public OwnerRecordStatus RecordStatus { get; }

    /// <summary>The credential gate tier from section 7.2, or null before it has been probed.</summary>
    public AuthTier? AuthTier { get; }

    /// <summary>The leftover sweep outcome, or null while the sweep has not run.</summary>
    public LeftoverSweepOutcome? Sweep { get; }

    /// <summary>The machine's child-session decision, or null while it has not been probed.</summary>
    public ChildSessionsDecision? ChildSessions { get; }

    /// <summary>
    /// Why the elevation attempt failed, or null when no elevation was attempted or it enabled the
    /// switch. The runner probes the machine again after a successful elevation, so success never
    /// leaves this value set.
    /// </summary>
    public ElevationFailure? ElevationFailure { get; }

    /// <summary>The agent attach outcome, or null while the agent has not been attempted.</summary>
    public AgentAttachOutcome? Agent { get; }
}
