namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>
/// Why one teardown step failed. The report carries only this enumeration: a failure never carries free
/// text, a path, an account identifier or a station name, so it can be reported as is.
/// </summary>
public enum TeardownFailureReason
{
    /// <summary>The step completed.</summary>
    None = 0,

    /// <summary>The logoff request for the child session was refused or threw.</summary>
    LogOffFailed = 1,

    /// <summary>The child-session switch could not be turned off or the read-back still reports it on.</summary>
    RestoreFailed = 2,

    /// <summary>The single-instance lock handle could not be closed or the lock still exists afterwards.</summary>
    ReleaseFailed = 3,

    /// <summary>The session could not be moved to <c>closing</c>.</summary>
    StateTransitionFailed = 4,

    /// <summary>
    /// The plan logs a child session off, but the context carries no positive child session id. The step
    /// fails with the meaning of <c>E_STATE</c>: the caller's own facts contradict its plan. It is a
    /// failure, never a silent skip, because an unlogged child session outlives the session that owns it.
    /// </summary>
    MissingChildSession = 5,
}
