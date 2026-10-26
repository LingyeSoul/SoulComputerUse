using System;

namespace SoulComputerUse.SessionAgent;

/// <summary>
/// The two Windows Hello facts the tier detector consumes. The caller supplies them because the
/// product's Windows layer declares that Hello availability and enrolment must come from a system
/// query; this library does not implement that query.
/// </summary>
/// <remarks>
/// <see cref="Unavailable"/> is the only pair this program can supply today. The agent targets the
/// base Windows framework, whose projections expose no Windows Hello availability query, so both
/// facts report false instead of guessing. Reporting absence keeps the agent on the child session's
/// own Windows logon gate and never claims the strong gate it did not verify. Wiring a real system
/// query replaces this value at the composition root; the detector itself does not change.
/// </remarks>
public sealed record AgentHelloFacts(
    Func<bool> AvailableAndEnrolled,
    Func<bool> HardwarePresentButNotEnrolled)
{
    /// <summary>The conservative pair: this program has no Windows Hello query to call.</summary>
    public static AgentHelloFacts Unavailable { get; } = new(NoHello, NoHello);

    private static bool NoHello()
    {
        return false;
    }
}