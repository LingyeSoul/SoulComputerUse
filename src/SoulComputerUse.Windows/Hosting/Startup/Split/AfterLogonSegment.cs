using System;
using System.Collections.Generic;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting.Startup.Phases;
using SoulComputerUse.Windows.Ipc;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.Windows.Hosting.Startup.Split;

/// <summary>
/// Everything that becomes possible only after the operator has logged on: attaching the agent pipe the
/// child session started as its alternate shell, recording the agent's Windows session, starting the owner
/// pipe and moving the ownership record through <c>connecting</c> to <c>ready</c>.
/// </summary>
/// <remarks>
/// <para>
/// This segment never connects either. The agent process exists only because the child session started it
/// after the operator logged on, so the connection has to be open before this segment runs; attaching
/// earlier would look for a pipe nobody has created yet on a production machine.
/// </para>
/// <para>
/// The order is the contract order: attach the agent, read the agent process's Windows session, write the
/// record with that session, start the owner pipe, then <c>connecting</c> and <c>ready</c>. The record is
/// written before the owner pipe because the agent's claim check reads it, and the record only becomes
/// <c>ready</c> once the pipe serves calls: a <c>ready</c> record without a pipe would tell a caller the
/// session serves tool calls when nothing is listening.
/// </para>
/// <para>
/// A failed step closes the agent connection and deletes the ownership record this start wrote, in that
/// order. Deleting is unconditional because the record is the start's own: the before-connect segment
/// wrote <c>starting</c> under the lock this process still holds, so any record on disk at this point
/// describes a session that never became ready. The lock is deliberately not released: it was taken by the
/// before-connect segment and belongs to the caller, which still needs the handle to clean up.
/// </para>
/// <para>
/// Every explanation is a fixed template plus an enumerated value or a count, so no failure carries a
/// path, an account, a SID or a nonce. This type writes no log entries.
/// </para>
/// </remarks>
public sealed class AfterLogonSegment : ISessionStartAfterLogon
{
    private readonly SessionStartOptions _options;
    private readonly IOwnerRecordStore _recordStore;
    private readonly IAgentConnector _agentConnector;
    private readonly IOwnerPipeHost _ownerPipeHost;
    private readonly IProcessSessionLookup _processSessionLookup;
    private readonly BeforeConnectResult _beforeConnect;
    private readonly Action<SessionState>? _stateChanged;
    private readonly SplitStepLog _steps = new();
    private readonly object _runtimeStateGate = new();

    private bool _ran;
    private AgentConnection? _agent;
    private int _subagentWtsSessionId;

    /// <summary>Creates one after-logon segment over the ports and the completed before-connect result.</summary>
    /// <exception cref="ArgumentNullException">The options, a port or the before-connect result is null.</exception>
    public AfterLogonSegment(
        SessionStartOptions options,
        IOwnerRecordStore recordStore,
        IAgentConnector agentConnector,
        IOwnerPipeHost ownerPipeHost,
        IProcessSessionLookup processSessionLookup,
        BeforeConnectResult beforeConnect)
        : this(
            options,
            recordStore,
            agentConnector,
            ownerPipeHost,
            processSessionLookup,
            beforeConnect,
            stateChanged: null)
    {
    }

    /// <summary>
    /// Creates one after-logon segment with a runtime-state observer. The observer is called after the
    /// ownership record has been updated, so a caller can refresh a window without racing the record.
    /// </summary>
    /// <exception cref="ArgumentNullException">The options, a port or the before-connect result is null.</exception>
    public AfterLogonSegment(
        SessionStartOptions options,
        IOwnerRecordStore recordStore,
        IAgentConnector agentConnector,
        IOwnerPipeHost ownerPipeHost,
        IProcessSessionLookup processSessionLookup,
        BeforeConnectResult beforeConnect,
        Action<SessionState>? stateChanged)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(recordStore);
        ArgumentNullException.ThrowIfNull(agentConnector);
        ArgumentNullException.ThrowIfNull(ownerPipeHost);
        ArgumentNullException.ThrowIfNull(processSessionLookup);
        ArgumentNullException.ThrowIfNull(beforeConnect);

        _options = options;
        _recordStore = recordStore;
        _agentConnector = agentConnector;
        _ownerPipeHost = ownerPipeHost;
        _processSessionLookup = processSessionLookup;
        _beforeConnect = beforeConnect;
        _stateChanged = stateChanged;
    }

    /// <summary>
    /// The attached agent connection after a successful segment, or null before or after a failure. The
    /// caller owns it on success and uses it to answer tool calls.
    /// </summary>
    public AgentConnection? AgentConnection => _agent;

    /// <summary>The stages this segment has executed so far, in contract order.</summary>
    public IReadOnlyList<SessionStartStepResult> SessionStartSteps => _steps.Snapshot;

    /// <summary>
    /// The Windows session id recorded from the attached agent, or zero before a successful read. The
    /// shutdown path uses this recorded value instead of asking the agent process again, so a process
    /// that has already exited cannot strand the ownership record or the lock.
    /// </summary>
    public int SubagentWtsSessionId => _subagentWtsSessionId;

    /// <summary>
    /// Attaches the agent, records its Windows session, starts the owner pipe and writes <c>ready</c>.
    /// </summary>
    /// <returns>The completed segment, including the agent's positive Windows session id.</returns>
    /// <exception cref="InvalidOperationException">The segment was already run, or the agent reported no positive process id.</exception>
    /// <exception cref="SessionStartException">
    /// The agent refused the attachment or a step failed. The exception carries the contract code and the
    /// stages that ran; the agent connection and the record this attempt wrote are already cleaned up, and
    /// the lock handle is left open because it belongs to the caller.
    /// </exception>
    public AfterLogonResult Execute()
    {
        if (_ran)
        {
            throw new InvalidOperationException(SessionStartSplitMessages.SegmentAlreadyRan);
        }

        _ran = true;

        try
        {
            _agent = AttachAgent();
            _subagentWtsSessionId = ReadAgentSession(_agent.AgentProcessId);
            WriteRecord(SessionState.Starting, SessionStartStage.WriteSubagentSession);
            StartOwnerPipe(_agent);
            WriteRecord(SessionState.Connecting, SessionStartStage.WriteConnecting);
            WriteRecord(SessionState.Ready, SessionStartStage.WriteReady);

            return new AfterLogonResult(
                agentAttached: true,
                ownerPipeStarted: true,
                subagentWtsSessionId: _subagentWtsSessionId,
                recordReady: true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw Cleanup(error);
        }
    }

    private AgentConnection AttachAgent()
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
                error);
        }

        if (!outcome.Connected)
        {
            SessionStartRefusal refusal = SessionStartOrchestrator.EvaluateAgent(outcome);
            _steps.Failed(SessionStartStage.ConnectAgent, refusal.Code);
            throw new SessionStartException(refusal.Code, refusal.Reason, _steps.Snapshot, innerException: null);
        }

        AgentConnection connection = outcome.Connection
            ?? throw new InvalidOperationException(
                "The agent connector reported a connected outcome without a connection.");

        _steps.Succeeded(SessionStartStage.ConnectAgent);
        return connection;
    }

    private int ReadAgentSession(int agentProcessId)
    {
        int sessionId;
        try
        {
            sessionId = _processSessionLookup.SessionIdFor(agentProcessId);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw StageFailure(
                SessionStartStage.WriteSubagentSession,
                SessionErrorCode.AgentDead,
                SessionStartMessages.AgentSessionUnreadable,
                error);
        }

        if (sessionId <= 0)
        {
            throw StageFailure(
                SessionStartStage.WriteSubagentSession,
                SessionErrorCode.AgentDead,
                SessionStartSplitMessages.AgentSessionNotPositive,
                new InvalidOperationException(
                    "The process session lookup returned a session id that names no interactive session, so the record could not name the child session."));
        }

        return sessionId;
    }

    private void StartOwnerPipe(AgentConnection agent)
    {
        try
        {
            // The owner pipe is the first component that sees a blocked desktop or a lost agent at
            // runtime, so it reports those transitions into the ownership record as they happen.
            _ownerPipeHost.Start(new SessionClientToolHandler(agent.Client, ReportRuntimeState));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw StageFailure(
                SessionStartStage.OwnerPipe,
                SessionErrorCode.ManualIntervention,
                SessionStartMessages.OwnerPipeFailed,
                error);
        }

        _steps.Succeeded(SessionStartStage.OwnerPipe);
    }

    private void WriteRecord(SessionState stateName, SessionStartStage stage)
    {
        try
        {
            _recordStore.Write(new SessionOwnerRecord(
                _options.SessionId,
                _options.HostPid,
                _options.HostProcessStartTime,
                _subagentWtsSessionId,
                _beforeConnect.ChildSessionsOpenedByUs,
                stateName,
                _options.AcquiredAt));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw StageFailure(
                stage,
                SessionErrorCode.ManualIntervention,
                SessionStartMessages.RecordWriteFailed,
                error);
        }

        _steps.Succeeded(stage);
    }

    /// <summary>
    /// Rewrites the ownership record with a runtime state observed by the owner pipe. The write is
    /// best effort: a failed status update must not replace the tool result, and the handler that owns
    /// the observer reports the failure.
    /// </summary>
    private void ReportRuntimeState(SessionState state)
    {
        if (_subagentWtsSessionId <= 0)
        {
            return;
        }

        lock (_runtimeStateGate)
        {
            _recordStore.Write(new SessionOwnerRecord(
                _options.SessionId,
                _options.HostPid,
                _options.HostProcessStartTime,
                _subagentWtsSessionId,
                _beforeConnect.ChildSessionsOpenedByUs,
                state,
                _options.AcquiredAt));
        }

        _stateChanged?.Invoke(state);
    }

    /// <summary>Records one failed stage and builds the refusal that reports it.</summary>
    private SessionStartException StageFailure(
        SessionStartStage stage,
        SessionErrorCode code,
        string reason,
        Exception innerException)
    {
        _steps.Failed(stage, code);
        return new SessionStartException(code, reason, _steps.Snapshot, innerException);
    }

    /// <summary>
    /// Closes the agent connection, then deletes the record this attempt wrote. Both steps are attempted
    /// even when one fails, and the lock handle is never touched here.
    /// </summary>
    private SessionStartException Cleanup(Exception error)
    {
        List<Exception> failures = [];
        if (_agent is not null)
        {
            try
            {
                if (_agent.Client is IAsyncDisposable disposable)
                {
                    disposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            catch (Exception closeError) when (closeError is not OutOfMemoryException)
            {
                failures.Add(closeError);
            }

            _agent = null;
        }

        try
        {
            // The delete is unconditional and idempotent: whichever record is on disk was written by this
            // start attempt, because the before-connect segment wrote `starting` under the lock this
            // process still holds. A session that never became ready must not leave a record that claims
            // otherwise.
            _recordStore.Delete();
        }
        catch (Exception deleteError) when (deleteError is not OutOfMemoryException)
        {
            failures.Add(deleteError);
        }

        SessionStartException reported = error as SessionStartException
            ?? new SessionStartException(
                SessionErrorCode.ManualIntervention,
                SessionStartMessages.UnexpectedStageFailure,
                _steps.Snapshot,
                error);

        if (failures.Count == 0)
        {
            return reported;
        }

        List<Exception> combined = [reported];
        combined.AddRange(failures);
        return new SessionStartException(
            reported.Code,
            reported.Message,
            reported.Steps,
            new AggregateException(SessionStartSplitMessages.AfterLogonCleanupFailed, combined));
    }
}
