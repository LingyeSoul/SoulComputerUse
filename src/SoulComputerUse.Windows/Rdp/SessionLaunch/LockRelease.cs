using System;
using System.Collections.Generic;
using SoulComputerUse.Windows.Hosting.Startup;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.Windows.Rdp.SessionLaunch;

/// <summary>
/// The production lock release: the ownership record is deleted and the handle closed, and the release
/// is confirmed by probing the machine-wide lock name.
/// </summary>
/// <remarks>
/// Both effects are attempted even when the first one fails, because a process that keeps the
/// machine-wide lock blocks every later session. A handle that was closed while the lock object
/// survived means another handle keeps it occupied, which is reported as a failure rather than as a
/// clean release.
/// </remarks>
public sealed class LockRelease : ILockRelease
{
    private readonly IOwnerRecordStore _recordStore;

    /// <summary>Creates the release over this account's ownership record.</summary>
    /// <exception cref="ArgumentNullException">The record store is null.</exception>
    public LockRelease(IOwnerRecordStore recordStore)
    {
        ArgumentNullException.ThrowIfNull(recordStore);
        _recordStore = recordStore;
    }

    /// <inheritdoc />
    /// <exception cref="AggregateException">Deleting the record, closing the handle, or the confirmation failed.</exception>
    public void Release(IDisposable lockHandle)
    {
        ArgumentNullException.ThrowIfNull(lockHandle);

        List<Exception> failures = [];
        try
        {
            _recordStore.Delete();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failures.Add(error);
        }

        bool closed = false;
        try
        {
            lockHandle.Dispose();
            closed = true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failures.Add(error);
        }

        if (closed && SingleInstanceLock.IsHeldByAnyone())
        {
            failures.Add(new InvalidOperationException(
                "The lock handle was closed, but probing the machine-wide lock name still reports it as held, so the lock was not released."));
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                "The session could not delete its ownership record, close its lock handle or confirm the lock was released.",
                failures);
        }
    }
}
