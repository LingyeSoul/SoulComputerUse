using System;

namespace SoulComputerUse.Windows.Hosting.Startup.Phases;

/// <summary>
/// The phase decision as a pure function of one facts snapshot: which phase the facts support, which
/// action comes next, and why.
/// </summary>
/// <remarks>
/// <para>
/// The decision walks the contract order and returns the first step the facts do not prove done, so a
/// start can never skip a step: sweep, the child-session switch, the alternate-shell properties, the
/// connection, the agent attachment, the owner pipe and the record's ready state.
/// </para>
/// <para>
/// The connection is checked before the agent on purpose. The agent is the child session's alternate
/// shell, so it starts only after the operator logs on in the remote window; facts that report an
/// attached agent while the connection is still closed describe an order the real machine cannot produce,
/// and the decision answers with <see cref="SessionStartPhaseAction.OpenConnection"/> and says so instead
/// of accepting the agent as attached.
/// </para>
/// <para>
/// Recording the agent's Windows session id has no boolean of its own, because the facts cannot observe
/// it separately from the attachment that produces it: the after-logon port reports the id together with
/// the attachment, and both later steps (the owner pipe and the ready record) can only happen after the
/// id is recorded. The decision therefore folds that step into the step after the attachment and never
/// asks for <see cref="SessionStartPhaseAction.WriteSubagentSession"/>; the action exists for the
/// execution layer, which reports the write as part of the after-logon phase.
/// </para>
/// </remarks>
public sealed record SessionStartPhasePlan
{
    /// <summary>Creates one facts snapshot. The facts are not required to be monotone.</summary>
    /// <remarks>
    /// A snapshot that reports a later step without an earlier one is accepted instead of rejected,
    /// because the decision's job is to name the earliest missing step: refusing such a snapshot would
    /// turn a recoverable ordering mistake into an exception that says nothing about the order.
    /// </remarks>
    public SessionStartPhasePlan(
        bool sweepCompleted,
        bool childSessionsSettled,
        bool alternateShellConfigured,
        bool connectionOpened,
        bool agentAttached,
        bool ownerPipeStarted,
        bool recordReady)
    {
        SweepCompleted = sweepCompleted;
        ChildSessionsSettled = childSessionsSettled;
        AlternateShellConfigured = alternateShellConfigured;
        ConnectionOpened = connectionOpened;
        AgentAttached = agentAttached;
        OwnerPipeStarted = ownerPipeStarted;
        RecordReady = recordReady;
    }

    /// <summary>Whether the leftover child-session sweep proved that nothing of ours is logged on.</summary>
    public bool SweepCompleted { get; }

    /// <summary>Whether the child-session switch is on and both policy values allow a connection.</summary>
    public bool ChildSessionsSettled { get; }

    /// <summary>Whether the alternate-shell properties point at the agent.</summary>
    public bool AlternateShellConfigured { get; }

    /// <summary>Whether the connection was opened through the single connection entry point.</summary>
    public bool ConnectionOpened { get; }

    /// <summary>Whether the agent pipe is attached.</summary>
    public bool AgentAttached { get; }

    /// <summary>Whether the owner pipe that carries tool calls is started.</summary>
    public bool OwnerPipeStarted { get; }

    /// <summary>Whether the ownership record is in <c>ready</c>.</summary>
    public bool RecordReady { get; }

    /// <summary>Decides the phase, the next action and the fixed explanation from these facts.</summary>
    public SessionStartPhaseDecision Decide()
    {
        if (!SweepCompleted)
        {
            return BeforeConnect(SessionStartPhaseAction.Sweep, SessionStartPhaseMessages.SweepPending);
        }

        if (!ChildSessionsSettled)
        {
            return BeforeConnect(
                SessionStartPhaseAction.EnableChildSessions,
                SessionStartPhaseMessages.ChildSessionsPending);
        }

        if (!AlternateShellConfigured)
        {
            return BeforeConnect(
                SessionStartPhaseAction.ConfigureAlternateShell,
                SessionStartPhaseMessages.AlternateShellPending);
        }

        if (!ConnectionOpened)
        {
            // An agent cannot exist before the connection that starts it, so the connection wins even
            // when the facts claim an attachment: opening it is the only way to reach the real order.
            string reason = AgentAttached
                ? SessionStartPhaseMessages.AgentBeforeConnection
                : SessionStartPhaseMessages.ConnectionPending;

            return BeforeConnect(SessionStartPhaseAction.OpenConnection, reason);
        }

        if (!AgentAttached)
        {
            return new SessionStartPhaseDecision(
                SessionStartPhase.AwaitingCredentials,
                SessionStartPhaseAction.AttachAgent,
                SessionStartPhaseMessages.AgentPending);
        }

        if (!OwnerPipeStarted)
        {
            return new SessionStartPhaseDecision(
                SessionStartPhase.AfterLogon,
                SessionStartPhaseAction.StartOwnerPipe,
                SessionStartPhaseMessages.OwnerPipePending);
        }

        if (!RecordReady)
        {
            return new SessionStartPhaseDecision(
                SessionStartPhase.AfterLogon,
                SessionStartPhaseAction.MarkReady,
                SessionStartPhaseMessages.RecordReadyPending);
        }

        return new SessionStartPhaseDecision(
            SessionStartPhase.Ready,
            nextAction: null,
            SessionStartPhaseMessages.Ready);
    }

    private static SessionStartPhaseDecision BeforeConnect(SessionStartPhaseAction action, string reason)
    {
        return new SessionStartPhaseDecision(SessionStartPhase.BeforeConnect, action, reason);
    }
}
