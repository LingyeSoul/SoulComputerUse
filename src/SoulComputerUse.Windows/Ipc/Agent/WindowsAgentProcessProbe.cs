using System;
using System.ComponentModel;
using System.Diagnostics;

namespace SoulComputerUse.Windows.Ipc.Agent;

/// <summary>
/// The production process probe. A process counts as alive only when it still exists and its start
/// time matches the value captured at connect time, so a reused process id is not accepted.
/// </summary>
public sealed class WindowsAgentProcessProbe : IAgentProcessProbe
{
    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="processId"/> is not positive.</exception>
    public bool TryIsAlive(int processId, DateTime processStartTimeUtc, out bool isAlive)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), processId, "The process id must be positive.");
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            DateTime actualStartTime = process.StartTime.ToUniversalTime();
            isAlive = actualStartTime == processStartTimeUtc;
            return true;
        }
        catch (ArgumentException)
        {
            isAlive = false;
            return true;
        }
        catch (InvalidOperationException)
        {
            isAlive = false;
            return true;
        }
        catch (Win32Exception)
        {
            isAlive = false;
            return false;
        }
        catch (NotSupportedException)
        {
            isAlive = false;
            return false;
        }
    }

    /// <summary>Captures the start time of a process for a later liveness probe.</summary>
    internal static bool TryGetStartTimeUtc(int processId, out DateTime startTimeUtc)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            startTimeUtc = process.StartTime.ToUniversalTime();
            return true;
        }
        catch (ArgumentException)
        {
            startTimeUtc = default;
            return false;
        }
        catch (InvalidOperationException)
        {
            startTimeUtc = default;
            return false;
        }
        catch (Win32Exception)
        {
            startTimeUtc = default;
            return false;
        }
        catch (NotSupportedException)
        {
            startTimeUtc = default;
            return false;
        }
    }
}