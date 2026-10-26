using System;

namespace SoulComputerUse.Windows.Rdp.SessionLaunch;

/// <summary>Releases the single-instance lock handle a ready start handed to the launch.</summary>
public interface ILockRelease
{
    /// <summary>
    /// Deletes this session's ownership record, then closes the lock handle. The record goes first
    /// because a record must never outlive the lock that makes it authoritative.
    /// </summary>
    /// <exception cref="Exception">The record could not be deleted or the handle could not be closed.</exception>
    void Release(IDisposable lockHandle);
}
