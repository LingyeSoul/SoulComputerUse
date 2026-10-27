using System;
using System.Diagnostics;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>
/// The contract's owner-liveness rule: a recorded owner is alive only while its process id still names
/// a running process whose start time matches the record, which is what separates a live owner from a
/// reused process id.
/// </summary>
/// <remarks>
/// The rule lives in one place because both the takeover decision and owner discovery ask it. The
/// record round-trips a timestamp while the process API reports local time to the millisecond, so the
/// start time is compared within a one-second window instead of demanding exact equality.
/// </remarks>
internal static class OwnerProcessLiveness
{
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);

    /// <summary>Reports whether the recorded owner process id is still in use by any process.</summary>
    internal static bool IsAlive(int hostPid)
    {
        if (hostPid <= 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(hostPid);
            return !process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reports whether the recorded start time still belongs to that process id, which is the contract's
    /// PID-reuse criterion. A different start time means the id now names another process.
    /// </summary>
    internal static bool DoesStartTimeMatch(int hostPid, DateTimeOffset hostProcessStartTime)
    {
        if (hostPid <= 0 || hostProcessStartTime == default)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(hostPid);
            DateTimeOffset actual = new(process.StartTime.ToUniversalTime());
            TimeSpan difference = actual - hostProcessStartTime;
            return difference.Duration() < StartTimeTolerance;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
