using System;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>
/// Closes the single-instance lock handle and confirms that the lock object is gone.
/// </summary>
/// <remarks>
/// Closing the handle is the contract's release step, but a caller must not report a clean shutdown on
/// the strength of the call alone: if the object still exists afterwards, another handle kept it alive
/// and the machine-wide lock is still occupied. That is a failed release, so it throws.
/// <para>
/// Only the process that owns the lock may release it. A handle that was refused because another process
/// holds the lock names a lock this process never acquired, and closing it would be a meaningless and
/// misleading "release", so the constructor rejects it.
/// </para>
/// </remarks>
public sealed class SingleInstanceLockRelease : ISingleInstanceLockRelease
{
    private readonly SingleInstanceLock _lockHandle;
    private readonly Func<bool> _isLockStillHeld;

    /// <summary>Creates the release over the lock this process owns.</summary>
    /// <param name="lockHandle">The acquired lock handle; its outcome must be accepted.</param>
    /// <exception cref="ArgumentNullException">The handle is null.</exception>
    /// <exception cref="ArgumentException">The handle does not hold the lock.</exception>
    /// <remarks>
    /// The confirmation probe is the contract-pinned machine-wide name, which is the only lock a
    /// production entry point ever holds.
    /// </remarks>
    public SingleInstanceLockRelease(SingleInstanceLock lockHandle)
        : this(lockHandle, SingleInstanceLock.IsHeldByAnyone)
    {
    }

    /// <summary>
    /// Creates the release over the lock this process owns and the probe that confirms the release.
    /// Internal so a test can probe a per-run lock name instead of the production name.
    /// </summary>
    internal SingleInstanceLockRelease(SingleInstanceLock lockHandle, Func<bool> isLockStillHeld)
    {
        ArgumentNullException.ThrowIfNull(lockHandle);
        ArgumentNullException.ThrowIfNull(isLockStillHeld);

        if (!lockHandle.IsHeld)
        {
            throw new ArgumentException(
                "The lock handle does not hold the single-instance lock, so this process has nothing to release.",
                nameof(lockHandle));
        }

        _lockHandle = lockHandle;
        _isLockStillHeld = isLockStillHeld;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The lock object still exists after the handle was closed.</exception>
    public void Release()
    {
        _lockHandle.Dispose();

        if (_isLockStillHeld())
        {
            throw new InvalidOperationException(
                "The single-instance lock handle was closed, but the lock object still exists, so the machine-wide lock is still occupied.");
        }
    }
}
