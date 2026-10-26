using System;

namespace SoulComputerUse.Windows.Hosting.Startup.Phases;

/// <summary>
/// What the after-logon phase proved after the operator logged on: the agent is attached, the agent's
/// Windows session id is recorded, the owner pipe is serving calls, and the record is ready.
/// </summary>
/// <remarks>
/// <para>
/// The constructor refuses a result that contradicts itself. A ready record needs the positive Windows
/// session id and a started owner pipe, because the record is what lets the agent pass its claim check
/// and what lets a caller reach the agent; a result claiming readiness without either would report a
/// session that cannot serve a tool call.
/// </para>
/// <para>
/// The Windows session id is required whenever an agent is attached, not only when the record is ready:
/// the id comes from the attached agent process, so a positive id and an attachment are the same fact
/// observed twice, and a discrepancy means one of them is wrong.
/// </para>
/// </remarks>
public sealed record AfterLogonResult
{
    /// <summary>Creates one after-logon result and refuses a value that contradicts itself.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The Windows session id is negative, or it is zero while an agent is reported attached.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The record is reported ready without an attached agent, a recorded Windows session or a started
    /// owner pipe.
    /// </exception>
    public AfterLogonResult(
        bool agentAttached,
        bool ownerPipeStarted,
        int subagentWtsSessionId,
        bool recordReady)
    {
        if (subagentWtsSessionId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(subagentWtsSessionId),
                subagentWtsSessionId,
                "A Windows session id is never negative; zero means no agent session has been recorded.");
        }

        if (agentAttached && subagentWtsSessionId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(subagentWtsSessionId),
                subagentWtsSessionId,
                "An attached agent has a Windows session, so its positive session id must be recorded with the attachment.");
        }

        if (!agentAttached && subagentWtsSessionId != 0)
        {
            throw new ArgumentException(
                "A Windows session id can only come from an attached agent process, so an unattached agent cannot carry one.",
                nameof(subagentWtsSessionId));
        }

        if (!agentAttached && ownerPipeStarted)
        {
            throw new ArgumentException(
                "The owner pipe serves calls through the agent, so it cannot start before the agent is attached.",
                nameof(ownerPipeStarted));
        }

        if (recordReady && !ownerPipeStarted)
        {
            throw new ArgumentException(
                "A ready record without a started owner pipe would report a session that a caller cannot reach.",
                nameof(recordReady));
        }

        AgentAttached = agentAttached;
        OwnerPipeStarted = ownerPipeStarted;
        SubagentWtsSessionId = subagentWtsSessionId;
        RecordReady = recordReady;
    }

    /// <summary>Whether the child-session agent pipe is attached.</summary>
    public bool AgentAttached { get; }

    /// <summary>Whether the owner pipe that carries tool calls is started.</summary>
    public bool OwnerPipeStarted { get; }

    /// <summary>
    /// The agent process's Windows session id, which the ownership record carries. Zero means no agent
    /// session has been recorded, which is only possible while no agent is attached.
    /// </summary>
    public int SubagentWtsSessionId { get; }

    /// <summary>Whether the ownership record is in <c>ready</c>.</summary>
    public bool RecordReady { get; }
}
