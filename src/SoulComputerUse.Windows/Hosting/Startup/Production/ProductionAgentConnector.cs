using System;
using System.Threading;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Ipc.Agent;

namespace SoulComputerUse.Windows.Hosting.Startup.Production;

/// <summary>
/// Attaches to the child-session agent pipe over the secured transport and reports the agent's process id
/// together with the connected client.
/// </summary>
/// <remarks>
/// Attaching performs the nonce handshake and the peer SID check inside the transport, so a returned
/// connection has already proved that the peer runs under this account and holds the binding nonce. The
/// port never opens a child session: opening belongs to the single connection entry point.
/// </remarks>
public sealed class ProductionAgentConnector : IAgentConnector
{
    private readonly string _sessionId;
    private readonly string _nonce;
    private readonly AgentClientTimings _timings;

    /// <summary>Creates the connector for one agent launch.</summary>
    /// <exception cref="ArgumentException">The session id or the nonce is empty.</exception>
    public ProductionAgentConnector(string sessionId, string nonce, AgentClientTimings timings)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("The session id must not be empty.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(nonce))
        {
            throw new ArgumentException("The binding nonce must not be empty.", nameof(nonce));
        }

        ArgumentNullException.ThrowIfNull(timings);
        _sessionId = sessionId;
        _nonce = nonce;
        _timings = timings;
    }

    /// <inheritdoc />
    public AgentAttachOutcome Attach()
    {
        AgentPipeClient client = new(
            AgentPipeNames.ForSession(_sessionId),
            _nonce,
            _timings,
            new WindowsAgentProcessProbe());
        try
        {
            SessionStatus status = client.GetStatusAsync(CancellationToken.None).GetAwaiter().GetResult();
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
            return AgentAttachOutcome.Failed(error.Code);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return AgentAttachOutcome.Failed(SessionErrorCode.Disconnected);
        }
    }
}