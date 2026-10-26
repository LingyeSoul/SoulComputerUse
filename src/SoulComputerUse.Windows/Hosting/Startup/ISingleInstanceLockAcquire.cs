using System;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>
/// The single effect the startup path needs from the lock: one non-waiting attempt that returns the
/// handle this process must keep until the session ends.
/// </summary>
/// <remarks>
/// The port exists so the startup pipeline can be exercised without contending for the contract's
/// machine-wide name; production passes an adapter over <see cref="SingleInstanceLock"/>. The port never
/// probes and never creates a temporary object: creating the lock and closing it again would leave a
/// window in which a second session sees no owner.
/// </remarks>
public interface ISingleInstanceLockAcquire
{
    /// <summary>Tries once to take the lock, without waiting and without a second attempt.</summary>
    /// <exception cref="Exception">The lock state could not be determined.</exception>
    LockAttempt Acquire();
}
