using System;
using System.Collections.Generic;
using SoulComputerUse.Windows.ChildSessions;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>
/// The complete result of one successful start: one stage result per executed or skipped stage, the
/// session values the record now carries, and the two resources the caller owns from here on.
/// </summary>
/// <remarks>
/// The caller owns <see cref="LockHandle"/> and <see cref="Agent"/> after a successful start: the lock
/// stays held for the whole session and the agent client stays open until shutdown. The runner does not
/// dispose either of them on success, which is why they travel in the report instead of being dropped.
/// </remarks>
public sealed record SessionStartReport
{
    /// <summary>Copies and validates the stage results.</summary>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The stage list is empty, a stage repeats, a stage leaves the pipeline order, or the values
    /// contradict each other.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The child session id is negative.</exception>
    public SessionStartReport(
        string sessionId,
        IReadOnlyList<SessionStartStepResult> steps,
        int subagentWtsSessionId,
        bool childSessionsOpenedByUs,
        IDisposable lockHandle,
        AgentConnection agent)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A start report must carry the session id.", nameof(sessionId));
        }

        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(lockHandle);
        ArgumentNullException.ThrowIfNull(agent);

        if (steps.Count == 0)
        {
            throw new ArgumentException("A start report needs at least one stage result.", nameof(steps));
        }

        if (subagentWtsSessionId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(subagentWtsSessionId),
                subagentWtsSessionId,
                "A ready start must have recorded the agent's positive Windows session id.");
        }

        SessionStartStepResult[] copy = new SessionStartStepResult[steps.Count];
        int previousRank = -1;
        for (int index = 0; index < steps.Count; index++)
        {
            SessionStartStepResult result = steps[index]
                ?? throw new ArgumentException("A start report cannot contain a null stage result.", nameof(steps));

            int rank = (int)result.Stage;
            if (rank <= previousRank)
            {
                throw new ArgumentException(
                    $"The result for the stage '{result.Stage}' is out of pipeline order or repeated.",
                    nameof(steps));
            }

            if (!result.Succeeded)
            {
                throw new ArgumentException(
                    "A successful start cannot carry a failed stage result.",
                    nameof(steps));
            }

            previousRank = rank;
            copy[index] = result;
        }

        SessionId = sessionId;
        Steps = Array.AsReadOnly(copy);
        SubagentWtsSessionId = subagentWtsSessionId;
        ChildSessionsOpenedByUs = childSessionsOpenedByUs;
        LockHandle = lockHandle;
        Agent = agent;
    }

    /// <summary>The session id shared by the record, the agent pipe and the owner pipe.</summary>
    public string SessionId { get; }

    /// <summary>One result per stage this attempt executed or explicitly skipped, in pipeline order.</summary>
    public IReadOnlyList<SessionStartStepResult> Steps { get; }

    /// <summary>The agent's Windows session id, which the final record carries.</summary>
    public int SubagentWtsSessionId { get; }

    /// <summary>
    /// Whether this start enabled the machine-wide child-session switch. False when the switch was
    /// already on, because an already-on switch belongs to whoever turned it on.
    /// </summary>
    public bool ChildSessionsOpenedByUs { get; }

    /// <summary>The lock handle that keeps the machine-wide lock held; owned by the caller now.</summary>
    public IDisposable LockHandle { get; }

    /// <summary>The connected agent client and its process id; owned by the caller now.</summary>
    public AgentConnection Agent { get; }
}
