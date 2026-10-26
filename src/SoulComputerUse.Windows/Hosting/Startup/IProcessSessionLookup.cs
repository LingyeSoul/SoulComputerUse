using System;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>Resolves the Windows Terminal Services session a process runs in.</summary>
/// <remarks>
/// The ownership record names the child session the agent runs in, and that value comes from the agent's
/// process id. The lookup is a port so the startup pipeline never depends on a real process table in a
/// test, and so a process that has already exited surfaces as an explicit failure.
/// </remarks>
public interface IProcessSessionLookup
{
    /// <summary>Returns the WTS session id of a live process.</summary>
    /// <param name="processId">The agent's process id; always positive.</param>
    /// <exception cref="ArgumentOutOfRangeException">The process id is not positive.</exception>
    /// <exception cref="InvalidOperationException">The process does not exist or its session cannot be read.</exception>
    int SessionIdFor(int processId);
}
