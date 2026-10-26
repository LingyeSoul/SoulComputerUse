using System;
using System.Diagnostics;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>
/// Reads the Windows Terminal Services session id of a live process, which is how the ownership record
/// learns the child session the agent runs in.
/// </summary>
/// <remarks>
/// A process that has already exited is an explicit failure: the record would otherwise name a session
/// that no longer exists, and the shutdown path would try to log off an unrelated session that later
/// reused the id.
/// </remarks>
public sealed class WindowsProcessSessionLookup : IProcessSessionLookup
{
    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The process id is not positive.</exception>
    /// <exception cref="InvalidOperationException">The process does not exist or its session cannot be read.</exception>
    public int SessionIdFor(int processId)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), processId, "A process id must be positive.");
        }

        int sessionId;
        try
        {
            using Process process = Process.GetProcessById(processId);
            sessionId = process.SessionId;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"The Windows session of the agent process {processId} could not be read, so the record cannot name the child session.",
                error);
        }

        if (sessionId <= 0)
        {
            throw new InvalidOperationException(
                $"The agent process {processId} reported Windows session id {sessionId}, which names no interactive session a record could carry.");
        }

        return sessionId;
    }
}
