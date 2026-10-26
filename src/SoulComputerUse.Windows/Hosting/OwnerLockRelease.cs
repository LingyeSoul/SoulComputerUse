using System;
using SoulComputerUse.Windows.SessionLifecycle.Teardown;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.Windows.Hosting;

/// <summary>
/// The lock-release step of a teardown, carried out by disposing the owner that holds the handle.
/// </summary>
/// <remarks>
/// Disposing the owner also stops the owner pipe and deletes the ownership record, which is the order
/// section 8 requires: the session stops being reachable before its record disappears. The release is
/// confirmed by probing the lock name, so a handle that somehow survived is reported as a failed step
/// rather than as a clean shutdown.
/// </remarks>
public sealed class OwnerLockRelease : ISingleInstanceLockRelease
{
    private readonly SessionOwnerService _owner;

    /// <summary>Creates the port over the owner that holds the lock.</summary>
    /// <exception cref="ArgumentNullException">The owner is null.</exception>
    public OwnerLockRelease(SessionOwnerService owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    /// <inheritdoc />
    public void Release()
    {
        _owner.DisposeAsync().AsTask().GetAwaiter().GetResult();

        if (SingleInstanceLock.IsHeldByAnyone(_owner.LockName))
        {
            throw new InvalidOperationException(
                "The owner was disposed, but probing the lock name still reports the single-instance lock as held.");
        }
    }
}