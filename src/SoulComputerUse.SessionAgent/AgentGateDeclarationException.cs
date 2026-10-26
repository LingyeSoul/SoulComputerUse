using System;

namespace SoulComputerUse.SessionAgent;

/// <summary>
/// The launcher declared a credential gate value that contradicts the tier this child session
/// detected. It is an <see cref="ArgumentException"/> because the command line is at fault, and it is
/// its own type so the entry point can tell it apart from a system argument failure that must stay a
/// start failure.
/// </summary>
/// <remarks>
/// The message names the contradiction and never repeats a declared value, a nonce or a path, because
/// any of those can reach a console, a screenshot or an issue report.
/// </remarks>
public sealed class AgentGateDeclarationException : ArgumentException
{
    /// <summary>Creates one refusal for a declaration that contradicts the detection.</summary>
    /// <param name="message">The contradiction, described without any of the values involved.</param>
    public AgentGateDeclarationException(string message)
        : base(message)
    {
    }
}