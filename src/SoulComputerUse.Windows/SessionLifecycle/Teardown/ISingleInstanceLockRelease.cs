using System;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>Closes the single-instance lock handle.</summary>
public interface ISingleInstanceLockRelease
{
    /// <summary>Releases the lock and confirms that this process no longer holds it.</summary>
    /// <exception cref="Exception">The handle could not be closed or the lock still exists afterwards.</exception>
    void Release();
}
