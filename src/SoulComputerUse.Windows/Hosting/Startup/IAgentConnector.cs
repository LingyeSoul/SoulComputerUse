using System;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>
/// Attaches to the child-session agent pipe as a port. The implementation performs the handshake,
/// verifies the peer and reports the agent's process id; the value it returns carries both.
/// </summary>
/// <remarks>
/// This is the only agent transport a start touches, and it is deliberately named differently from the
/// remote desktop entry point: this port attaches to a pipe that already exists and must never open a
/// child session. Opening the session belongs to the single connection entry point, which the host's
/// interface layer calls after this pipeline is ready.
/// </remarks>
public interface IAgentConnector
{
    /// <summary>Attaches to the agent pipe and reports the client plus the agent process id.</summary>
    /// <returns>Either a connection or the contract code the connector observed.</returns>
    /// <exception cref="Exception">The connector failed outside the contract's error codes.</exception>
    AgentAttachOutcome Attach();
}
