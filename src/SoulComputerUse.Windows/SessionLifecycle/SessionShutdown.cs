using System;
using System.Collections.Generic;

namespace SoulComputerUse.Windows.SessionLifecycle;

/// <summary>
/// The ordered teardown steps from section 8 of the interface contract. The numeric values are the
/// contract order, and the plan validates against that order so a caller cannot release the lock
/// before the child session is gone.
/// </summary>
public enum SessionShutdownStep
{
    /// <summary>Move the session state to <c>closing</c> before any destructive work starts.</summary>
    EnterClosing = 0,

    /// <summary>Log off the child session this session opened, if one exists.</summary>
    LogOffChildSession = 1,

    /// <summary>Restore the child-session system switch, but only when this session turned it on.</summary>
    RestoreChildSessionsSwitch = 2,

    /// <summary>Close the single-instance lock handle.</summary>
    ReleaseLock = 3,

    /// <summary>Exit the process with the exit code computed from the step results.</summary>
    Exit = 4,
}

/// <summary>What the shutdown planner needs to know about this session.</summary>
public sealed record SessionShutdownRequest(
    bool ChildSessionsOpenedByUs,
    bool ChildSessionNeedsLogOff,
    bool LockIsHeld);

/// <summary>
/// An immutable, ordered shutdown plan. The constructor rejects an empty or out-of-order sequence, so
/// a plan that exists always follows the contract's teardown order.
/// </summary>
public sealed record SessionShutdownPlan
{
    /// <summary>Copies and validates the ordered steps.</summary>
    public SessionShutdownPlan(IReadOnlyList<SessionShutdownStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        if (steps.Count == 0)
        {
            throw new ArgumentException("A shutdown plan needs at least one step.", nameof(steps));
        }

        SessionShutdownStep[] copy = new SessionShutdownStep[steps.Count];
        HashSet<SessionShutdownStep> seen = new();
        int previousRank = -1;
        for (int index = 0; index < steps.Count; index++)
        {
            SessionShutdownStep step = steps[index];
            if (!Enum.IsDefined(step))
            {
                throw new ArgumentOutOfRangeException(nameof(steps), step, "Unknown shutdown step.");
            }

            if (!seen.Add(step))
            {
                throw new ArgumentException($"The shutdown step '{step}' appears more than once.", nameof(steps));
            }

            int rank = RankOf(step);
            if (rank <= previousRank)
            {
                throw new ArgumentException(
                    $"The shutdown step '{step}' is out of contract order; it must come after the previous step.",
                    nameof(steps));
            }

            previousRank = rank;
            copy[index] = step;
        }

        if (copy[0] != SessionShutdownStep.EnterClosing)
        {
            throw new ArgumentException("A shutdown plan must enter the closing state first.", nameof(steps));
        }

        if (copy[^1] != SessionShutdownStep.Exit)
        {
            throw new ArgumentException("A shutdown plan must exit last.", nameof(steps));
        }

        if (!seen.Contains(SessionShutdownStep.ReleaseLock))
        {
            throw new ArgumentException(
                "A shutdown plan must always release the single-instance lock.",
                nameof(steps));
        }

        Steps = Array.AsReadOnly(copy);
    }

    /// <summary>The teardown steps in the only order the contract allows.</summary>
    public IReadOnlyList<SessionShutdownStep> Steps { get; }

    private static int RankOf(SessionShutdownStep step)
    {
        return step switch
        {
            SessionShutdownStep.EnterClosing => 0,
            SessionShutdownStep.LogOffChildSession => 1,
            SessionShutdownStep.RestoreChildSessionsSwitch => 2,
            SessionShutdownStep.ReleaseLock => 3,
            SessionShutdownStep.Exit => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Unknown shutdown step."),
        };
    }
}

/// <summary>
/// The shutdown half of the session lifecycle from section 8: enter <c>closing</c>, log off the child
/// session, restore the child-session switch only when this session turned it on, release the lock and
/// exit. Step execution stays with the caller; this type only orders the work and maps failures to an
/// exit code.
/// </summary>
public static class SessionShutdown
{
    /// <summary>Builds the ordered teardown plan for this session.</summary>
    public static SessionShutdownPlan Plan(SessionShutdownRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.LockIsHeld)
        {
            throw new InvalidOperationException(
                "The shutdown sequence may only run in the process that owns the single-instance lock; another process must not log off this session's child session or release its lock.");
        }

        List<SessionShutdownStep> steps = new()
        {
            SessionShutdownStep.EnterClosing,
        };

        if (request.ChildSessionNeedsLogOff)
        {
            steps.Add(SessionShutdownStep.LogOffChildSession);
        }

        if (request.ChildSessionsOpenedByUs)
        {
            steps.Add(SessionShutdownStep.RestoreChildSessionsSwitch);
        }

        steps.Add(SessionShutdownStep.ReleaseLock);
        steps.Add(SessionShutdownStep.Exit);

        return new SessionShutdownPlan(steps);
    }

    /// <summary>
    /// Maps the teardown result to the process exit code. A single failed step makes the whole shutdown
    /// fail, so leftovers can never be reported as a clean exit. The caller should still attempt the
    /// remaining steps, especially releasing the lock, and then report the failure with this code.
    /// </summary>
    public static int ExitCodeFor(bool anyStepFailed)
    {
        return anyStepFailed ? SessionLifecycleExitCodes.Failure : SessionLifecycleExitCodes.Success;
    }
}
