using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.ChildSessions;
using SoulComputerUse.Windows.Ipc;
using SoulComputerUse.Windows.SessionLifecycle.Teardown;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>
/// Carries out one complete session start by following the steps
/// <see cref="SessionStartOrchestrator.Decide"/> asks for: take the lock, judge the ownership record and
/// write it in <c>starting</c>, probe the credential tier, sweep the leftovers, probe and if necessary
/// enable the child-session switch, connect the agent, record the agent's Windows session, start the
/// owner pipe, then move the record through <c>connecting</c> to <c>ready</c>.
/// </summary>
/// <remarks>
/// <para>
/// This type never opens a remote desktop connection. Opening the child session belongs to
/// <see cref="Rdp.ChildSessionConnection.Connect"/>, which the host's interface layer calls only after a
/// start reports ready; a start that connected here would create a second connection path that could
/// skip the sweep. The runner also never accepts an executable path, because every port derives its own
/// path from the application directory.
/// </para>
/// <para>
/// Every port is synchronous, so the pipeline performs no thread switch and awaits nothing while a
/// caller's synchronization context could deadlock it. <see cref="RunAsync"/> therefore runs the
/// pipeline directly and wraps the result.
/// </para>
/// <para>
/// A failed stage cleans up unconditionally: the agent connection is closed, the ownership record this
/// attempt wrote is deleted, and the lock handle is released. The record is deleted before the lock
/// because a record must never outlive the lock that makes it authoritative. A record this attempt never
/// wrote is deliberately left on disk, since it is the only evidence of the previous owner.
/// </para>
/// <para>
/// This type writes no log entries. Its exception message is the fixed reason of the refused gate, and
/// every reason is a template plus an enumeration or a count, so no path, account, SID or nonce appears.
/// </para>
/// </remarks>
public sealed class SessionStartRunner
{
    private readonly SessionStartOptions _options;
    private readonly ISingleInstanceLockAcquire _lockAcquire;
    private readonly IOwnerRecordStore _recordStore;
    private readonly IAuthTierSource _authTierSource;
    private readonly ILeftoverSweep _leftoverSweep;
    private readonly IChildSessionsGate _childSessionsGate;
    private readonly IAgentConnector _agentConnector;
    private readonly IOwnerPipeHost _ownerPipeHost;
    private readonly IProcessSessionLookup _processSessionLookup;

    /// <summary>Creates one runner over the ports a start needs.</summary>
    /// <exception cref="ArgumentNullException">The options or a port is null.</exception>
    public SessionStartRunner(
        SessionStartOptions options,
        ISingleInstanceLockAcquire lockAcquire,
        IOwnerRecordStore recordStore,
        IAuthTierSource authTierSource,
        ILeftoverSweep leftoverSweep,
        IChildSessionsGate childSessionsGate,
        IAgentConnector agentConnector,
        IOwnerPipeHost ownerPipeHost,
        IProcessSessionLookup processSessionLookup)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(lockAcquire);
        ArgumentNullException.ThrowIfNull(recordStore);
        ArgumentNullException.ThrowIfNull(authTierSource);
        ArgumentNullException.ThrowIfNull(leftoverSweep);
        ArgumentNullException.ThrowIfNull(childSessionsGate);
        ArgumentNullException.ThrowIfNull(agentConnector);
        ArgumentNullException.ThrowIfNull(ownerPipeHost);
        ArgumentNullException.ThrowIfNull(processSessionLookup);

        _options = options;
        _lockAcquire = lockAcquire;
        _recordStore = recordStore;
        _authTierSource = authTierSource;
        _leftoverSweep = leftoverSweep;
        _childSessionsGate = childSessionsGate;
        _agentConnector = agentConnector;
        _ownerPipeHost = ownerPipeHost;
        _processSessionLookup = processSessionLookup;
    }

    /// <summary>Runs the whole start and returns the report of a session that is ready to serve.</summary>
    /// <param name="cancellationToken">
    /// Checked once before the first effect. A start is deliberately not cancellable part way through,
    /// because an abandoned start would leave a held lock, a written record or an enabled switch behind.
    /// </param>
    /// <returns>The report of a ready start; the caller owns the lock handle and the agent connection.</returns>
    /// <exception cref="OperationCanceledException">The token was already cancelled, so nothing ran.</exception>
    /// <exception cref="SessionStartException">
    /// A gate refused the start or a stage failed. The exception carries the code and the stage results.
    /// </exception>
    public Task<SessionStartReport> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Run());
    }

    private SessionStartReport Run()
    {
        List<SessionStartStepResult> steps = [];
        LockAttempt attempt = AcquireLock(steps);

        IDisposable lockHandle = attempt.Handle
            ?? throw new InvalidOperationException(
                "The lock port reported that this process acquired the lock without returning the handle that keeps it alive.");

        StartState state = new(lockHandle);
        try
        {
            RunPipeline(steps, state);

            AgentConnection agent = state.Agent
                ?? throw new InvalidOperationException(
                    "The startup pipeline reached the ready state without an agent connection.");

            return new SessionStartReport(
                _options.SessionId,
                steps.AsReadOnly(),
                state.SubagentWtsSessionId,
                state.ChildSessionsOpenedByUs,
                lockHandle,
                agent);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            SessionStartException reported = error as SessionStartException
                ?? new SessionStartException(
                    SessionErrorCode.ManualIntervention,
                    SessionStartMessages.UnexpectedStageFailure,
                    steps.AsReadOnly(),
                    error);

            List<Exception> cleanupFailures = Cleanup(state);
            throw WithCleanupFailures(reported, cleanupFailures);
        }
    }

    /// <summary>
    /// Follows <see cref="SessionStartOrchestrator.Decide"/> step by step: each iteration decides from the
    /// facts collected so far and runs exactly the step the plan names, until the plan asks to run the
    /// session or a gate refuses.
    /// </summary>
    private void RunPipeline(List<SessionStartStepResult> steps, StartState state)
    {
        state.RecordStatus = ReadOwnerRecord(steps);

        while (true)
        {
            SessionStartPlan plan = SessionStartOrchestrator.Decide(state.ToFacts());
            if (!plan.AllowsStart)
            {
                throw Failure(plan, steps, innerException: null);
            }

            switch (plan.NextStep)
            {
                case SessionStartStep.Sweep:
                    RunPreConnectionGates(steps, state, plan);
                    break;

                case SessionStartStep.EnableChildSessions:
                    EnableChildSessions(steps, state);
                    break;

                case SessionStartStep.ConnectAgent:
                    ConnectAgent(steps, state);
                    break;

                case SessionStartStep.Run:
                    RunReady(steps, state);
                    return;

                case SessionStartStep.None:
                    throw new InvalidOperationException(
                        "The startup decision allowed the session to go on without naming the next step.");

                default:
                    throw new InvalidOperationException(
                        "The startup decision named a step the pipeline does not define.");
            }
        }
    }

    /// <summary>
    /// Runs everything the plan's sweep step stands for, in the contract order: replace a leftover record
    /// that may be taken over, write <c>starting</c>, probe the credential tier, sweep the leftover child
    /// sessions, then probe the machine's child-session state.
    /// </summary>
    /// <remarks>
    /// Each gate refuses as soon as it is judged, so a <c>none</c> tier is refused before anything is
    /// swept and a failed sweep is refused before the machine's switch is read. The refusals reuse the
    /// gate functions of <see cref="SessionStartOrchestrator"/>, so no rule is stated twice.
    /// </remarks>
    private void RunPreConnectionGates(
        List<SessionStartStepResult> steps,
        StartState state,
        SessionStartPlan plan)
    {
        if (plan.DeletesStaleRecord)
        {
            DeleteLeftoverRecord(steps);
            state.RecordStatus = OwnerRecordStatus.Absent;
        }

        WriteRecord(SessionState.Starting, SessionStartStage.WriteStartingRecord, steps, state);
        RunAuthTierStage(steps, state);
        RunSweepStage(steps, state);
        RunChildSessionsStage(steps, state);
    }

    /// <summary>Probes the credential tier and refuses immediately when no verification path exists.</summary>
    private void RunAuthTierStage(List<SessionStartStepResult> steps, StartState state)
    {
        AuthTier tier;
        try
        {
            tier = _authTierSource.Detect();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw StageFailure(
                SessionStartStage.AuthTier,
                SessionErrorCode.ManualIntervention,
                SessionStartMessages.AuthTierProbeFailed,
                steps,
                error);
        }

        SessionStartRefusal? refusal = SessionStartOrchestrator.EvaluateAuthTier(tier);
        if (refusal is not null)
        {
            steps.Add(Failed(SessionStartStage.AuthTier, refusal.Code));
            throw Failure(refusal, steps, innerException: null);
        }

        state.AuthTier = tier;
        steps.Add(Succeeded(SessionStartStage.AuthTier));
    }

    /// <summary>Sweeps the leftover child sessions and refuses immediately when one could not be removed.</summary>
    private void RunSweepStage(List<SessionStartStepResult> steps, StartState state)
    {
        LeftoverSweepOutcome sweep;
        Exception? sweepError = null;
        try
        {
            sweep = LeftoverSweepOutcome.Completed(_leftoverSweep.Sweep());
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The sweep's own report travels on the exception, so a refusal can still say how far the
            // attempt got. The exception itself is kept as the inner failure of the refusal.
            sweepError = error;
            sweep = LeftoverSweepOutcome.Failed(error.Data[LeftoverSweeper.ReportDataKey] as LeftoverSweepReport);
        }

        SessionStartRefusal? refusal = SessionStartOrchestrator.EvaluateSweep(sweep);
        if (refusal is not null)
        {
            steps.Add(Failed(SessionStartStage.Sweep, refusal.Code));
            throw Failure(refusal, steps, sweepError);
        }

        state.Sweep = sweep;
        steps.Add(Succeeded(SessionStartStage.Sweep));
    }

    /// <summary>Probes the child-session switch and both policy values, and refuses a denied policy.</summary>
    /// <remarks>
    /// A denied policy verdict belongs to the probe stage: the value is what the machine answered, so the
    /// stage that read it carries the refusal code instead of a later stage pretending to have failed.
    /// </remarks>
    private void RunChildSessionsStage(List<SessionStartStepResult> steps, StartState state)
    {
        bool enabled;
        try
        {
            enabled = _childSessionsGate.IsEnabled();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw StageFailure(
                SessionStartStage.ChildSessions,
                SessionErrorCode.ManualIntervention,
                SessionStartMessages.ChildSessionsProbeFailed,
                steps,
                error);
        }

        PolicyState rdpInbound = ReadPolicy(steps, PolicyValue.RdpInbound);
        PolicyState childConnections = ReadPolicy(steps, PolicyValue.ChildConnections);

        ChildSessionsDecision decision = ChildSessionsPolicy.Evaluate(enabled, rdpInbound, childConnections);
        SessionStartRefusal? refusal = SessionStartOrchestrator.EvaluateChildSessions(decision);
        if (refusal is not null)
        {
            steps.Add(Failed(SessionStartStage.ChildSessions, refusal.Code));
            throw Failure(refusal, steps, innerException: null);
        }

        state.ChildSessions = decision;
        steps.Add(Succeeded(SessionStartStage.ChildSessions));
    }

    /// <summary>Reads one policy value and refuses under the code that owns that value.</summary>
    private PolicyState ReadPolicy(List<SessionStartStepResult> steps, PolicyValue value)
    {
        try
        {
            return _childSessionsGate.ReadPolicy(value);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A stored value that is not a DWORD is consent that cannot be read, which the contract puts
            // under the code that owns that policy value.
            SessionErrorCode code = value switch
            {
                PolicyValue.RdpInbound => SessionErrorCode.RdpInboundDenied,
                PolicyValue.ChildConnections => SessionErrorCode.ChildConnectionsDenied,
                _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown policy value."),
            };

            throw StageFailure(
                SessionStartStage.ChildSessions,
                code,
                SessionStartMessages.PolicyValueMalformed(value),
                steps,
                error);
        }
    }

    /// <summary>Records one failed stage, then builds the refusal that reports it.</summary>
    private static SessionStartException StageFailure(
        SessionStartStage stage,
        SessionErrorCode code,
        string reason,
        List<SessionStartStepResult> steps,
        Exception innerException)
    {
        steps.Add(Failed(stage, code));
        return Failure(new SessionStartRefusal(code, reason), steps, innerException);
    }

    /// <summary>Asks for administrator consent, enables the switch, and records that this session did it.</summary>
    private void EnableChildSessions(List<SessionStartStepResult> steps, StartState state)
    {
        ElevationResult result;
        try
        {
            result = _childSessionsGate.Enable();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw StageFailure(
                SessionStartStage.EnableChildSessions,
                SessionErrorCode.ElevationUnavailable,
                SessionStartMessages.ElevationUnavailable,
                steps,
                error);
        }

        if (result.Failed)
        {
            ElevationFailure failure = result.Failure
                ?? throw new InvalidOperationException(
                    "The elevation result reported a failure without naming the enumerated reason.");

            SessionStartRefusal refusal = SessionStartOrchestrator.EvaluateElevation(failure);
            steps.Add(Failed(SessionStartStage.EnableChildSessions, refusal.Code));
            throw Failure(refusal, steps, innerException: null);
        }

        steps.Add(Succeeded(SessionStartStage.EnableChildSessions));

        // The elevation result alone is not proof: a helper that reported success while the switch stayed
        // off would let the pipeline connect with the capability still missing.
        bool enabled;
        try
        {
            enabled = _childSessionsGate.IsEnabled();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw StageFailure(
                SessionStartStage.ChildSessions,
                SessionErrorCode.ChildSessionsDisabled,
                SessionStartMessages.ChildSessionsProbeFailed,
                steps,
                error);
        }

        if (!enabled)
        {
            throw StageFailure(
                SessionStartStage.ChildSessions,
                SessionErrorCode.ChildSessionsDisabled,
                SessionStartMessages.ChildSessionsStayedOff,
                steps,
                new InvalidOperationException(
                    "The elevation helper reported success, but reading the switch back still reports child sessions as disabled."));
        }

        state.ChildSessionsOpenedByUs = true;
        state.ChildSessions = new ChildSessionsDecision(
            ChildSessionsDecisionKind.Proceed,
            Value: null,
            Problem: null,
            Message: null);
        WriteRecord(SessionState.Starting, SessionStartStage.RecordChildSessions, steps, state);
    }

    /// <summary>Connects the agent pipe and passes its own contract code through on failure.</summary>
    private void ConnectAgent(List<SessionStartStepResult> steps, StartState state)
    {
        AgentAttachOutcome outcome;
        try
        {
            outcome = _agentConnector.Attach();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw StageFailure(
                SessionStartStage.ConnectAgent,
                SessionErrorCode.Disconnected,
                SessionStartMessages.AgentConnectThrew,
                steps,
                error);
        }

        if (!outcome.Connected)
        {
            SessionStartRefusal refusal = SessionStartOrchestrator.EvaluateAgent(outcome);
            state.AgentOutcome = outcome;
            steps.Add(Failed(SessionStartStage.ConnectAgent, refusal.Code));
            throw Failure(refusal, steps, innerException: null);
        }

        state.AgentOutcome = outcome;
        state.Agent = outcome.Connection;
        steps.Add(Succeeded(SessionStartStage.ConnectAgent));
    }

    /// <summary>Records the agent's Windows session, starts the owner pipe and finishes in <c>ready</c>.</summary>
    private void RunReady(List<SessionStartStepResult> steps, StartState state)
    {
        AgentConnection agent = state.Agent
            ?? throw new InvalidOperationException(
                "The startup decision allowed the session to run without an agent connection.");

        state.SubagentWtsSessionId = ReadAgentSession(steps, agent.AgentProcessId);
        WriteRecord(SessionState.Starting, SessionStartStage.WriteSubagentSession, steps, state);
        StartOwnerPipe(steps, agent);
        WriteRecord(SessionState.Connecting, SessionStartStage.WriteConnecting, steps, state);
        WriteRecord(SessionState.Ready, SessionStartStage.WriteReady, steps, state);
    }

    private LockAttempt AcquireLock(List<SessionStartStepResult> steps)
    {
        LockAttempt attempt;
        try
        {
            attempt = _lockAcquire.Acquire();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new SessionStartException(
                SessionErrorCode.ManualIntervention,
                SessionStartMessages.LockProbeFailed,
                steps.AsReadOnly(),
                error);
        }

        SessionStartRefusal? refusal = SessionStartOrchestrator.EvaluateLock(attempt.Outcome);
        if (refusal is not null)
        {
            throw Failure(refusal, steps, innerException: null);
        }

        steps.Add(Succeeded(SessionStartStage.Lock));
        return attempt;
    }

    private OwnerRecordStatus ReadOwnerRecord(List<SessionStartStepResult> steps)
    {
        OwnerRecordRead record;
        try
        {
            record = _recordStore.Read();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw StageFailure(
                SessionStartStage.ReadOwnerRecord,
                SessionErrorCode.LockStaleRecord,
                SessionStartMessages.RecordReadFailed,
                steps,
                error);
        }

        steps.Add(Succeeded(SessionStartStage.ReadOwnerRecord));
        return record.Status;
    }

    private void DeleteLeftoverRecord(List<SessionStartStepResult> steps)
    {
        try
        {
            _recordStore.Delete();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw StageFailure(
                SessionStartStage.TakeOverRecord,
                SessionErrorCode.LockStaleRecord,
                SessionStartMessages.RecordReadFailed,
                steps,
                error);
        }

        steps.Add(Succeeded(SessionStartStage.TakeOverRecord));
    }

    private int ReadAgentSession(List<SessionStartStepResult> steps, int agentProcessId)
    {
        try
        {
            return _processSessionLookup.SessionIdFor(agentProcessId);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw StageFailure(
                SessionStartStage.WriteSubagentSession,
                SessionErrorCode.AgentDead,
                SessionStartMessages.AgentSessionUnreadable,
                steps,
                error);
        }
    }

    private void StartOwnerPipe(List<SessionStartStepResult> steps, AgentConnection agent)
    {
        try
        {
            _ownerPipeHost.Start(new SessionClientToolHandler(agent.Client));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw StageFailure(
                SessionStartStage.OwnerPipe,
                SessionErrorCode.ManualIntervention,
                SessionStartMessages.OwnerPipeFailed,
                steps,
                error);
        }

        steps.Add(Succeeded(SessionStartStage.OwnerPipe));
    }

    private void WriteRecord(
        SessionState stateName,
        SessionStartStage stage,
        List<SessionStartStepResult> steps,
        StartState state)
    {
        try
        {
            _recordStore.Write(new SessionOwnerRecord(
                _options.SessionId,
                _options.HostPid,
                _options.HostProcessStartTime,
                state.SubagentWtsSessionId,
                state.ChildSessionsOpenedByUs,
                stateName,
                _options.AcquiredAt));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw StageFailure(
                stage,
                SessionErrorCode.ManualIntervention,
                SessionStartMessages.RecordWriteFailed,
                steps,
                error);
        }

        state.RecordWritten = true;
        steps.Add(Succeeded(stage));
    }

    /// <summary>
    /// Closes the agent connection, deletes the record this attempt wrote, then releases the lock. Every
    /// step is attempted even when an earlier one fails, and the failures are returned instead of being
    /// swallowed so the caller reports them beside the original refusal.
    /// </summary>
    private List<Exception> Cleanup(StartState state)
    {
        List<Exception> failures = [];
        if (state.Agent is not null)
        {
            try
            {
                (state.Agent.Client as IAsyncDisposable)?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failures.Add(error);
            }
        }

        if (state.RecordWritten)
        {
            try
            {
                _recordStore.Delete();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failures.Add(error);
            }
        }

        if (!state.LockReleased)
        {
            try
            {
                state.LockHandle.Dispose();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failures.Add(error);
            }

            state.LockReleased = true;
        }

        return failures;
    }

    private static SessionStartException Failure(
        SessionStartPlan plan,
        List<SessionStartStepResult> steps,
        Exception? innerException)
    {
        SessionErrorCode code = plan.ErrorCode
            ?? throw new InvalidOperationException("A refused startup plan must carry a contract error code.");

        return new SessionStartException(code, plan.Reason, steps.AsReadOnly(), innerException);
    }

    private static SessionStartException Failure(
        SessionStartRefusal refusal,
        List<SessionStartStepResult> steps,
        Exception? innerException)
    {
        return new SessionStartException(refusal.Code, refusal.Reason, steps.AsReadOnly(), innerException);
    }

    private static SessionStartException WithCleanupFailures(
        SessionStartException reported,
        List<Exception> cleanupFailures)
    {
        if (cleanupFailures.Count == 0)
        {
            return reported;
        }

        List<Exception> combined = [reported];
        combined.AddRange(cleanupFailures);
        return new SessionStartException(
            reported.Code,
            reported.Message,
            reported.Steps,
            new AggregateException(
                "The startup pipeline failed and its cleanup also failed, so the ownership record or the lock may remain.",
                combined));
    }

    private static SessionStartStepResult Succeeded(SessionStartStage stage)
    {
        return new SessionStartStepResult(stage, executed: true, succeeded: true, errorCode: null);
    }

    private static SessionStartStepResult Failed(SessionStartStage stage, SessionErrorCode code)
    {
        return new SessionStartStepResult(stage, executed: true, succeeded: false, code);
    }

    /// <summary>The mutable bookkeeping of one attempt; it never leaves the runner.</summary>
    private sealed class StartState
    {
        internal StartState(IDisposable lockHandle)
        {
            LockHandle = lockHandle;
            RecordStatus = OwnerRecordStatus.Absent;
        }

        internal IDisposable LockHandle { get; }

        internal OwnerRecordStatus RecordStatus { get; set; }

        internal AuthTier? AuthTier { get; set; }

        internal LeftoverSweepOutcome? Sweep { get; set; }

        internal ChildSessionsDecision? ChildSessions { get; set; }

        internal AgentAttachOutcome? AgentOutcome { get; set; }

        internal int SubagentWtsSessionId { get; set; }

        internal bool ChildSessionsOpenedByUs { get; set; }

        internal bool RecordWritten { get; set; }

        internal bool LockReleased { get; set; }

        internal AgentConnection? Agent { get; set; }

        internal SessionStartFacts ToFacts()
        {
            return new SessionStartFacts(
                LockAcquireOutcome.Acquired,
                RecordStatus,
                AuthTier,
                Sweep,
                ChildSessions,
                null,
                AgentOutcome);
        }
    }
}
