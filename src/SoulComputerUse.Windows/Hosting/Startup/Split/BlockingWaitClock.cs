using System;
using System.Threading;

namespace SoulComputerUse.Windows.Hosting.Startup.Split;

/// <summary>
/// The production wait clock: a blocking sleep on the calling thread, which is the thread the synchronous
/// start pipeline already owns while the window waits for the operator to log on.
/// </summary>
/// <remarks>
/// This type exists so the waiter itself never calls a sleep: the wait is a port, and the only place that
/// really blocks is this one, where nothing else is happening.
/// </remarks>
public sealed class BlockingWaitClock : IWaitClock
{
    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The duration is not positive.</exception>
    public void Wait(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "A wait interval must be positive.");
        }

        Thread.Sleep(duration);
    }
}
