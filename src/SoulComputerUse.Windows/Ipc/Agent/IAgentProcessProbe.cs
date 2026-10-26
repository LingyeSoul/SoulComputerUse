using System;

namespace SoulComputerUse.Windows.Ipc.Agent;

/// <summary>
/// Tells the client whether the agent process it connected to is still alive. The start time is part
/// of the question so a reused process id is not mistaken for the original agent.
/// </summary>
public interface IAgentProcessProbe
{
    /// <summary>
    /// Probes one process. The return value says whether the probe could be performed at all; when it
    /// is false the caller must not claim the process is dead.
    /// </summary>
    /// <param name="processId">The process id captured when the pipe connected.</param>
    /// <param name="processStartTimeUtc">The start time captured when the pipe connected.</param>
    /// <param name="isAlive">The probe result; meaningful only when the method returns true.</param>
    bool TryIsAlive(int processId, DateTime processStartTimeUtc, out bool isAlive);
}