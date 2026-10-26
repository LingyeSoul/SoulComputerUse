using System;
using System.Threading;
using System.Threading.Tasks;

namespace SoulComputerUse.Windows.Rdp.SessionWatch;

/// <summary>
/// The two clock functions a watch needs: a monotonic reading and a cancellable wait. Keeping both
/// behind one port lets a test drive a whole timeout scenario without waiting for wall-clock time.
/// </summary>
public interface IWatchClock
{
    /// <summary>The monotonic time that has passed since this clock was created.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>Waits for the delay, or returns early when the token is cancelled.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}