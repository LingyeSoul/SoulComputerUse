using System;
using System.Threading;
using SoulComputerUse.Windows.Hosting.Startup.Split;
using SoulComputerUse.Windows.Native;

namespace SoulComputerUse.Windows.Shell;

/// <summary>
/// Pumps the calling thread's window messages without blocking on <c>GetMessage</c>, so a window keeps
/// painting and answering while a caller waits for something else.
/// </summary>
/// <remarks>
/// A wait that blocks the thread that owns the product window freezes that window: the remote desktop
/// control cannot paint the Windows logon screen, the caption never repaints, and the operator sees a
/// white rectangle while being asked to log on. The poller therefore dispatches every pending message
/// first and only then yields the thread for a short slice.
/// </remarks>
public static class MessagePump
{
    /// <summary>The longest the poller yields when no message is pending.</summary>
    private const int IdleSliceMilliseconds = 10;

    /// <summary>The window message that ends a message loop.</summary>
    private const uint WmQuit = 0x0012;

    /// <summary>
    /// Dispatches every message that is already queued and returns whether any was handled.
    /// <c>WM_QUIT</c> is left in the queue so the caller can notice it.
    /// </summary>
    public static bool PumpOnce()
    {
        bool handled = false;
        while (NativeMethods.PeekMessageW(
                   out Msg message,
                   0,
                   0,
                   0,
                   NativeMethods.PmRemove))
        {
            if (message.Message == WmQuit)
            {
                // Put the quit back so the real loop still sees it; a poller must not swallow shutdown.
                NativeMethods.PostQuitMessage((int)message.WParam);
                return handled;
            }

            _ = NativeMethods.TranslateMessage(ref message);
            _ = NativeMethods.DispatchMessageW(ref message);
            handled = true;
        }

        return handled;
    }

    /// <summary>
    /// Pumps messages for one duration, or until <paramref name="stopRequested"/> reports true. The
    /// caller keeps control of when the wait ends, so a closed window can end it immediately.
    /// </summary>
    /// <param name="duration">How long to keep pumping; must be positive.</param>
    /// <param name="stopRequested">Reports whether the wait may end early.</param>
    /// <exception cref="ArgumentNullException">The stop probe is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The duration is not positive.</exception>
    public static void PumpFor(TimeSpan duration, Func<bool> stopRequested)
    {
        ArgumentNullException.ThrowIfNull(stopRequested);
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "A pump duration must be positive.");
        }

        long deadline = Environment.TickCount64 + (long)duration.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (stopRequested())
            {
                return;
            }

            if (!PumpOnce())
            {
                Thread.Sleep(IdleSliceMilliseconds);
            }
        }
    }
}

/// <summary>
/// The wait clock a windowed start uses: instead of sleeping, it pumps messages for the whole interval,
/// so the remote desktop control keeps painting the logon screen while the logon gate is polled.
/// </summary>
/// <remarks>
/// The window's destruction ends the wait early: a caller that closed the window must not be kept
/// waiting for a logon that will never be reported.
/// </remarks>
public sealed class PumpingWaitClock : IWaitClock
{
    private readonly Func<bool> _stopRequested;

    /// <summary>Creates the clock over a probe that reports whether the wait may end early.</summary>
    /// <exception cref="ArgumentNullException">The probe is null.</exception>
    public PumpingWaitClock(Func<bool> stopRequested)
    {
        ArgumentNullException.ThrowIfNull(stopRequested);
        _stopRequested = stopRequested;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The duration is not positive.</exception>
    public void Wait(TimeSpan duration)
    {
        MessagePump.PumpFor(duration, _stopRequested);
    }
}