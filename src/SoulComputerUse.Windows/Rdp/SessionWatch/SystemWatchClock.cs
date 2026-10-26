using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SoulComputerUse.Windows.Rdp.SessionWatch;

/// <summary>The clock the product runs on: a monotonic stopwatch and a real cancellable delay.</summary>
public sealed class SystemWatchClock : IWatchClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    /// <inheritdoc />
    public TimeSpan Elapsed => _stopwatch.Elapsed;

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The delay is negative.</exception>
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "The wait delay must not be negative.");
        }

        if (delay == TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        return Task.Delay(delay, cancellationToken);
    }
}