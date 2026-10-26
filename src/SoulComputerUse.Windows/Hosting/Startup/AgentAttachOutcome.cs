using System;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>The agent connection this process established, together with the agent's own process id.</summary>
/// <remarks>
/// The client is handed over to the caller on success, so the runner never disposes it in that case.
/// The process id is what the ownership record needs to name the child session the agent runs in; the
/// runner deliberately does not read it itself, because only the connector knows how the handshake
/// exposes it.
/// </remarks>
public sealed record AgentConnection
{
    /// <summary>Creates a connection around the agent client and the agent process id.</summary>
    /// <exception cref="ArgumentNullException">The client is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The process id is not positive.</exception>
    public AgentConnection(ISessionClient client, int agentProcessId)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (agentProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(agentProcessId),
                agentProcessId,
                "An agent connection needs the agent's positive process id.");
        }

        Client = client;
        AgentProcessId = agentProcessId;
    }

    /// <summary>The connected agent client; the caller owns it after a successful start.</summary>
    public ISessionClient Client { get; }

    /// <summary>The agent process id, which names the Windows session the agent runs in.</summary>
    public int AgentProcessId { get; }
}

/// <summary>
/// The outcome of trying to connect the agent pipe. A failure carries the contract code the connector
/// observed, so the decision can pass it through instead of inventing a broader one.
/// </summary>
public sealed record AgentAttachOutcome
{
    private AgentAttachOutcome(AgentConnection? connection, SessionErrorCode? failureCode)
    {
        Connection = connection;
        FailureCode = failureCode;
    }

    /// <summary>The established connection, or null when the attempt failed.</summary>
    public AgentConnection? Connection { get; }

    /// <summary>The contract code of the failure, or null when the connection was established.</summary>
    public SessionErrorCode? FailureCode { get; }

    /// <summary>Whether the agent pipe is connected and usable.</summary>
    public bool Connected => Connection is not null;

    /// <summary>Creates a successful outcome around one connection.</summary>
    /// <exception cref="ArgumentNullException">The connection is null.</exception>
    public static AgentAttachOutcome Succeeded(AgentConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new AgentAttachOutcome(connection, null);
    }

    /// <summary>Creates a failed outcome around the code the connector observed.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The code is not a defined value.</exception>
    public static AgentAttachOutcome Failed(SessionErrorCode failureCode)
    {
        if (!Enum.IsDefined(failureCode))
        {
            throw new ArgumentOutOfRangeException(nameof(failureCode), failureCode, "Unknown session error code.");
        }

        return new AgentAttachOutcome(null, failureCode);
    }
}
