using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Ipc.Agent;
using SoulComputerUse.Windows.Native;
using SoulComputerUse.Windows.SessionLifecycle;
using SoulComputerUse.Windows.SessionLifecycle.Teardown;

namespace SoulComputerUse.Windows.Hosting;

/// <summary>
/// A host process attached to one already-running child-session agent. It composes the agent pipe
/// client, the owner tool handler and the owner service, then exposes the agent's status for the
/// product window. The client is created by the caller so tests can use unique pipes and timings.
/// </summary>
public sealed class AgentAttachedSession : IAsyncDisposable
{
    private readonly AgentPipeClient _client;
    private readonly SessionOwnerService _owner;
    private bool _childSessionsOpenedByUs;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates one attached session over an existing agent client and owner service.</summary>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public AgentAttachedSession(AgentPipeClient client, SessionOwnerService owner)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(owner);
        _client = client;
        _owner = owner;
    }

    /// <summary>
    /// The agent's status after a successful <see cref="Start"/>; null before the session starts.
    /// </summary>
    public SessionStatus? Status { get; private set; }

    /// <summary>
    /// Raised after a runtime transition observed by the owner pipe, for example <c>blocked</c> or
    /// <c>disconnected</c>. The event is raised on the pipe's worker thread.
    /// </summary>
    public event Action<SessionState>? StateChanged;

    /// <summary>
    /// Builds the production attached session: the owner pipe, the owner record and the agent pipe
    /// client all use the supplied session id and binding nonce.
    /// </summary>
    /// <exception cref="ArgumentException">The session id or nonce is empty.</exception>
    public static AgentAttachedSession CreateForCurrentProcess(string sessionId, string nonce)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("The session id must not be empty.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(nonce))
        {
            throw new ArgumentException("The binding nonce must not be empty.", nameof(nonce));
        }

        AgentPipeClient client = new(
            AgentPipeNames.ForSession(sessionId),
            nonce,
            AgentClientTimings.Contract,
            new WindowsAgentProcessProbe());

        // The owner is built after its handler, so the runtime-state observer reads the owner through
        // a captured variable. Only a lost or blocked session is forwarded; a repeated ready status
        // must not attempt a ready -> ready transition.
        SessionOwnerService? owner = null;
        AgentAttachedSession? session = null;
        SessionClientToolHandler handler = new(client, state =>
        {
            if (owner is null
                || state is not (SessionState.Ready or SessionState.Blocked or SessionState.Disconnected)
                || !SessionStateMachine.CanTransition(owner.CurrentState, state))
            {
                return;
            }

            owner.Transition(state);
            session?.NotifyStateChanged(state);
        });
        owner = SessionOwnerService.ForCurrentProcess(handler, sessionId);
        session = new AgentAttachedSession(client, owner);
        return session;
    }

    private void NotifyStateChanged(SessionState state)
    {
        StateChanged?.Invoke(state);
    }

    /// <summary>
    /// Starts the owner pipe, reads the agent's status and moves the owner record through
    /// <c>connecting</c> to <c>ready</c>. A failure cleans up both halves before it is rethrown.
    /// </summary>
    /// <remarks>
    /// The work runs on the thread pool on purpose: the owner pipe client and the WPF dispatcher both
    /// use a synchronization context, so awaiting them on the dispatcher thread while blocking on the
    /// result would deadlock. Callers that are already asynchronous should use
    /// <see cref="StartAsync"/> instead.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The session was already started or the agent is not ready.</exception>
    /// <exception cref="SessionOperationException">The agent pipe or the owner pipe failed.</exception>
    public void Start()
    {
        Task.Run(() => StartAsync()).GetAwaiter().GetResult();
    }

    /// <summary>The asynchronous form of <see cref="Start"/>.</summary>
    /// <exception cref="InvalidOperationException">The session was already started or the agent is not ready.</exception>
    /// <exception cref="SessionOperationException">The agent pipe or the owner pipe failed.</exception>
    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException("The attached session was already started.");
        }

        _owner.Start();
        try
        {
            SessionStatus status = await _client.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            status.Validate();
            if (!string.Equals(status.State, "ready", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The child-session agent reported state '{status.State}', but an attached session must be ready.");
            }

            int serverProcessId = _client.ServerProcessId;
            if (serverProcessId <= 0)
            {
                throw new InvalidOperationException("The agent pipe did not report a server process id after the handshake.");
            }

            int childSessionId;
            using (Process agentProcess = Process.GetProcessById(serverProcessId))
            {
                childSessionId = agentProcess.SessionId;
            }

            _owner.SetSubagentWtsSessionId(childSessionId);
            _owner.Transition(SessionState.Connecting);
            _owner.Transition(SessionState.Ready);
            Status = status;
            _started = true;
        }
        catch
        {
            await _owner.DisposeAsync().ConfigureAwait(false);
            await _client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Stops the owner pipe first, then closes the agent pipe.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _owner.DisposeAsync().ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Marks that this session turned the machine-wide child-session switch on, so the shutdown plan
    /// includes restoring it. A session that merely found the switch already on must not call this.
    /// </summary>
    public void MarkChildSessionsOpenedByUs()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _childSessionsOpenedByUs = true;
    }

    /// <summary>
    /// Runs the contract's shutdown sequence: enter <c>closing</c>, log the child session off, restore
    /// the switch only when this session opened it, release the lock, then let the caller exit.
    /// </summary>
    /// <remarks>
    /// A failed step never stops the sequence, and the returned report carries the non-zero exit code
    /// the caller must use. This method never ends the process itself.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The session was not started.</exception>
    public async Task<int> CloseAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started)
        {
            throw new InvalidOperationException("The attached session has not started, so it has nothing to shut down.");
        }

        int childSessionId = _owner.SubagentWtsSessionId;
        if (childSessionId > 0)
        {
            // Logging off the console session would sign the operator out of their own desktop, which is
            // unrecoverable. A recorded child id equal to the console id therefore means the id was
            // obtained wrongly, and the shutdown is refused before any destructive step runs.
            int consoleSessionId = WtsSessionProbe.GetActiveConsoleSessionId();
            if (childSessionId == consoleSessionId)
            {
                throw new InvalidOperationException(
                    "The recorded child session id is the active console session id, so logging it off would sign the operator out. The shutdown is refused.");
            }
        }

        SessionShutdownRequest request = new(
            ChildSessionsOpenedByUs: _childSessionsOpenedByUs,
            ChildSessionNeedsLogOff: childSessionId > 0,
            LockIsHeld: true);
        SessionShutdownPlan plan = SessionShutdown.Plan(request);
        SessionTeardownContext context = new(
            childSessionId > 0 ? childSessionId : null,
            _childSessionsOpenedByUs,
            LockIsHeld: true,
            ProcessExitRequested: true);

        SessionTeardownExecutor executor = new(
            new OwnerStateTransition(_owner),
            new WtsChildSessionLogOff(new WtsSessionTable()),
            new ChildSessionsSwitchRestore(),
            new OwnerLockRelease(_owner));

        SessionTeardownReport report = await executor.ExecuteAsync(plan, context, cancellationToken).ConfigureAwait(false);

        // The child session is gone or the logoff was refused; either way the agent pipe can no longer
        // carry a valid session, so it is closed after the plan rather than before the logoff.
        await _client.DisposeAsync().ConfigureAwait(false);
        return report.ExitCode;
    }
}