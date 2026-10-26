using System;
using System.Diagnostics;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Ipc.Agent;

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
        SessionClientToolHandler handler = new(client);
        SessionOwnerService owner = SessionOwnerService.ForCurrentProcess(handler, sessionId);
        return new AgentAttachedSession(client, owner);
    }

    /// <summary>
    /// Starts the owner pipe, reads the agent's status and moves the owner record through
    /// <c>connecting</c> to <c>ready</c>. A failure cleans up both halves before it is rethrown.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session was already started or the agent is not ready.</exception>
    /// <exception cref="SessionOperationException">The agent pipe or the owner pipe failed.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException("The attached session was already started.");
        }

        _owner.Start();
        try
        {
            SessionStatus status = _client.GetStatusAsync(default).GetAwaiter().GetResult();
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
            _owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
}