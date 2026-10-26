using System;

namespace SoulComputerUse.Windows.Hosting.Startup.Split;

/// <summary>
/// The wait one connection observation needs, as a clock the caller supplies.
/// </summary>
/// <remarks>
/// <para>
/// The waiter never sleeps on its own, so a test can pass a clock that returns at once and still exercise
/// the same loop, and a production caller can pass <see cref="BlockingWaitClock"/>, which really waits.
/// This also keeps the after-connection wait independent of the session-watch clock used elsewhere: the two
/// wait on different things and must not share a type that could grow a second meaning.
/// </para>
/// </remarks>
public interface IWaitClock
{
    /// <summary>Waits for one interval before the next observation.</summary>
    /// <param name="duration">The interval; always positive.</param>
    void Wait(TimeSpan duration);
}
