using System;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>One attempt at the machine-wide single-instance lock.</summary>
/// <remarks>
/// The handle is the whole occupancy: holding it keeps the lock object alive, and closing it releases
/// the lock from any thread. A held-elsewhere attempt therefore carries no handle, because this process
/// never acquired anything it could release.
/// </remarks>
public sealed record LockAttempt
{
    private LockAttempt(LockAcquireOutcome outcome, IDisposable? handle)
    {
        Outcome = outcome;
        Handle = handle;
    }

    /// <summary>What the attempt reported.</summary>
    public LockAcquireOutcome Outcome { get; }

    /// <summary>The handle that keeps the lock alive, or null when another process holds it.</summary>
    public IDisposable? Handle { get; }

    /// <summary>Creates the result of a successful attempt.</summary>
    /// <exception cref="ArgumentNullException">The handle is null.</exception>
    public static LockAttempt Acquired(IDisposable handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return new LockAttempt(LockAcquireOutcome.Acquired, handle);
    }

    /// <summary>Creates the result of an attempt that found the lock already held.</summary>
    public static LockAttempt HeldElsewhere()
    {
        return new LockAttempt(LockAcquireOutcome.HeldElsewhere, null);
    }
}
