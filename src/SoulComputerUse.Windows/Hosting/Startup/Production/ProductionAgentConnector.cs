using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting.Startup.Split;
using SoulComputerUse.Windows.Ipc.Agent;

namespace SoulComputerUse.Windows.Hosting.Startup.Production;

/// <summary>
/// Attaches to the child-session agent pipe over the secured transport and reports the agent's process id
/// together with the connected client.
/// </summary>
/// <remarks>
/// <para>
/// Attaching performs the nonce handshake and the peer SID check inside the transport, so a returned
/// connection has already proved that the peer runs under this account and holds the binding nonce. The
/// port never opens a child session: opening belongs to the single connection entry point.
/// </para>
/// <para>
/// The attach is retried for a bounded period because the pipe does not exist the moment the operator logs
/// on: the agent is started by that logon, and it runs its claim gate, its window-station gate, its tier
/// detection and the shell bootstrap before it binds the pipe. A single attempt that gave up after the
/// connect timeout would refuse a session that is merely still starting.
/// </para>
/// </remarks>
public sealed class ProductionAgentConnector : IAgentConnector
{
    /// <summary>How long the agent may take to bind its pipe after the operator logs on.</summary>
    private static readonly TimeSpan AttachWindow = TimeSpan.FromSeconds(45);

    private readonly string _sessionId;
    private readonly string _nonce;
    private readonly AgentClientTimings _timings;
    private readonly IWaitClock _wait;
    private readonly TimeSpan _attachWindow;

    /// <summary>Creates the connector for one agent launch, waiting on the caller's blocking clock.</summary>
    /// <exception cref="ArgumentException">The session id or the nonce is empty.</exception>
    public ProductionAgentConnector(string sessionId, string nonce, AgentClientTimings timings)
        : this(sessionId, nonce, timings, new BlockingWaitClock())
    {
    }

    /// <summary>Creates the connector for one agent launch with an explicit wait clock.</summary>
    /// <exception cref="ArgumentException">The session id or the nonce is empty.</exception>
    /// <exception cref="ArgumentNullException">The timings or the wait clock is null.</exception>
    public ProductionAgentConnector(
        string sessionId,
        string nonce,
        AgentClientTimings timings,
        IWaitClock wait)
        : this(sessionId, nonce, timings, wait, AttachWindow)
    {
    }

    /// <summary>
    /// Creates the connector for one agent launch with an explicit wait clock and attach window, which a
    /// test scales down so a refusal is observed without waiting the production window.
    /// </summary>
    /// <exception cref="ArgumentException">The session id, the nonce or the attach window is empty.</exception>
    /// <exception cref="ArgumentNullException">The timings or the wait clock is null.</exception>
    public ProductionAgentConnector(
        string sessionId,
        string nonce,
        AgentClientTimings timings,
        IWaitClock wait,
        TimeSpan attachWindow)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("The session id must not be empty.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(nonce))
        {
            throw new ArgumentException("The binding nonce must not be empty.", nameof(nonce));
        }

        if (attachWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(attachWindow), attachWindow, "The attach window must be positive.");
        }

        ArgumentNullException.ThrowIfNull(timings);
        ArgumentNullException.ThrowIfNull(wait);
        _sessionId = sessionId;
        _nonce = nonce;
        _timings = timings;
        _wait = wait;
        _attachWindow = attachWindow;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A <c>disconnected</c> outcome means the pipe was not there yet, so it is retried until the attach
    /// window closes. Every other refusal is final: a peer that rejected the SID check or the nonce must
    /// not be retried, because the same values would be rejected again.
    /// <para>
    /// Each retry waits on the injected clock, and so does each attempt itself, which is what keeps the
    /// product window alive: this runs on the thread that owns the window, so a plain blocking wait here
    /// would freeze the caption, the buttons and the remote desktop control for the whole connect timeout
    /// of every attempt, and the window would look unresponsive while the agent is still starting.
    /// </para>
    /// </remarks>
    public AgentAttachOutcome Attach()
    {
        long deadline = Environment.TickCount64 + (long)_attachWindow.TotalMilliseconds;
        AgentAttachOutcome outcome = Attempt();
        while (!outcome.Connected
            && outcome.FailureCode == SessionErrorCode.Disconnected
            && Environment.TickCount64 < deadline)
        {
            _wait.Wait(AttachRetryInterval);
            outcome = Attempt();
        }

        Trace(outcome.Connected
            ? "connected"
            : "failed code=" + SessionErrorCodeNames.ToWireName(outcome.FailureCode!.Value));
        return outcome;
    }

    /// <summary>How long one failed attempt waits before the pipe is tried again.</summary>
    private static readonly TimeSpan AttachRetryInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long one attempt's slices may run while the pipe call is in flight.</summary>
    /// <remarks>
    /// The pipe call bounds itself with the contract timeouts, so this ceiling only stops a call that
    /// none of them ended; it is the sum of the connect, handshake and action timeouts plus a small
    /// margin, and reaching it is reported as an ordinary failed attempt.
    /// </remarks>
    private TimeSpan AttemptCeiling => _timings.ConnectTimeout + _timings.HandshakeTimeout + _timings.ActionTimeout + AttemptSlice;

    /// <summary>How long one slice of an attempt waits on the clock before looking at the call again.</summary>
    private static readonly TimeSpan AttemptSlice = TimeSpan.FromMilliseconds(100);

    private AgentAttachOutcome Attempt()
    {
        AgentPipeClient client = new(
            AgentPipeNames.ForSession(_sessionId),
            _nonce,
            _timings,
            new WindowsAgentProcessProbe());
        try
        {
            SessionStatus status = WaitOnClock(client.GetStatusAsync(CancellationToken.None));
            status.Validate();
            int agentProcessId = client.ServerProcessId;
            if (agentProcessId <= 0)
            {
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw new InvalidOperationException(
                    "The agent pipe handshake succeeded without reporting the agent process id, so the child session cannot be recorded.");
            }

            return AgentAttachOutcome.Succeeded(new AgentConnection(client, agentProcessId));
        }
        catch (SessionOperationException error)
        {
            DisposeQuietly(client);
            return AgentAttachOutcome.Failed(error.Code);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            DisposeQuietly(client);
            Trace("failed reason=unexpected type=" + error.GetType().Name);
            return AgentAttachOutcome.Failed(SessionErrorCode.Disconnected);
        }
    }

    /// <summary>
    /// Waits for one pipe call on the injected clock, in slices, instead of blocking the thread.
    /// </summary>
    /// <remarks>
    /// The call itself already fails with its contract timeout, so the ceiling here is a guard for a call
    /// that outlives them all; a caller whose clock pumps window messages keeps the product window
    /// answering for the whole attempt.
    /// </remarks>
    private T WaitOnClock<T>(Task<T> call)
    {
        long deadline = Environment.TickCount64 + (long)AttemptCeiling.TotalMilliseconds;
        while (!call.IsCompleted)
        {
            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException(
                    "The agent pipe call did not finish within the sum of its contract timeouts.");
            }

            _wait.Wait(AttemptSlice);
        }

        return call.GetAwaiter().GetResult();
    }

    private static void DisposeQuietly(AgentPipeClient client)
    {
        try
        {
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A failed attempt's client is already unusable, and a failure to dispose it must not hide
            // the attach outcome that the caller has to act on.
        }
    }

    /// <summary>
    /// Reports the attach outcome with diagnostics on. A child session whose alternate shell never started
    /// leaves nothing to connect to, and the difference between "no pipe" and "a pipe that refused the
    /// handshake" decides whether the fault is the launch or the security gate.
    /// </summary>
    private static void Trace(string outcome)
    {
        if (Environment.GetEnvironmentVariable("SOULCOMPUTERUSE_DIAGNOSTICS") == "1")
        {
            Console.Error.WriteLine(
                "trace agent-attach " + outcome
                + " at=" + DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
        }
    }
}