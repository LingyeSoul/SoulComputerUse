using System;

namespace SoulComputerUse.Windows.Native;

/// <summary>Where an agent process turned out to be running.</summary>
public enum AgentSessionVerdict
{
    /// <summary>The agent is in a session that is not the operator's console session.</summary>
    ChildSession = 0,

    /// <summary>The agent is in the console session, that is, on the operator's own desktop.</summary>
    ConsoleSession = 1,
}

/// <summary>
/// Pure decision behind the agent's identity gate. Kept free of interop so the rule can be
/// asserted without a real second session.
/// </summary>
public static class AgentSessionGate
{
    /// <summary>
    /// An agent that lands in the console session must exit at once: its screenshots and input
    /// would hit the operator's real desktop. Any failure to read either session id is handled by
    /// the caller as a fail-closed exit, so this function only has to compare two known ids.
    /// </summary>
    public static AgentSessionVerdict Evaluate(int processSessionId, int consoleSessionId)
    {
        if (processSessionId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processSessionId),
                processSessionId,
                "A Windows Terminal Services session id cannot be negative.");
        }

        if (consoleSessionId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(consoleSessionId),
                consoleSessionId,
                "A Windows Terminal Services session id cannot be negative.");
        }

        return processSessionId == consoleSessionId ? AgentSessionVerdict.ConsoleSession : AgentSessionVerdict.ChildSession;
    }
}