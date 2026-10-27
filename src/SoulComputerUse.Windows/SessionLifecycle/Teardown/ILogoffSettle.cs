using System;
using System.Threading;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>
/// How the leftover sweep waits for a logoff request to take effect.
/// </summary>
/// <remarks>
/// A Windows logoff is asynchronous: the session stays listed in the session table for several seconds
/// after a request that succeeded. A sweep that read the table once would therefore report every real
/// leftover as "survived the logoff request" and refuse the start for no reason. The wait is a port so a
/// test can run the same loop without spending the time.
/// </remarks>
public interface ILogoffSettle
{
    /// <summary>Waits for one confirmation interval before the session table is read again.</summary>
    /// <param name="interval">The interval; always positive.</param>
    void Wait(TimeSpan interval);
}

/// <summary>
/// The production settle: a real wait on the thread that is already shutting the session down, so no other
/// work is blocked by it.
/// </summary>
public sealed class ThreadLogoffSettle : ILogoffSettle
{
    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The interval is not positive.</exception>
    public void Wait(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "A wait interval must be positive.");
        }

        Thread.Sleep(interval);
    }
}