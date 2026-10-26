using System;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>The facts about this session that the teardown executor needs to carry out a plan.</summary>
/// <param name="ChildWtsSessionId">
/// The Windows session id of the child session this session opened, or <c>null</c> when there is none.
/// A plan that logs a child session off requires a positive value.
/// </param>
/// <param name="ChildSessionsOpenedByUs">
/// Whether this session turned the machine-wide child-session switch on. Restoring the switch is only
/// legal when it is true.
/// </param>
/// <param name="LockIsHeld">Whether this process currently holds the machine-wide single-instance lock.</param>
/// <param name="ProcessExitRequested">
/// Whether the caller will exit the process once the teardown report exists. The teardown layer never
/// calls <see cref="Environment.Exit(int)"/> itself.
/// </param>
public sealed record SessionTeardownContext(
    int? ChildWtsSessionId,
    bool ChildSessionsOpenedByUs,
    bool LockIsHeld,
    bool ProcessExitRequested);
