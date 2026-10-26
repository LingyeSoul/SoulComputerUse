using System;
using System.Collections.Generic;
using SoulComputerUse.Windows.SessionLifecycle;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>
/// The complete result of executing one shutdown plan: one entry per planned step, in plan order.
/// </summary>
/// <remarks>
/// A failed step never stops the sequence, so the report is the whole story: a caller can see that the
/// logoff failed and that the lock was released anyway. <see cref="ExitCode"/> is the contract's mapping
/// of any failure to a non-zero process exit code.
/// <para>
/// The report carries no free text. <see cref="LockWasHeld"/> and <see cref="ProcessExitRequested"/>
/// restate the context the plan ran against, so the caller can tell a real failure from a context that
/// never described a shutdown.
/// </para>
/// </remarks>
public sealed record SessionTeardownReport
{
    /// <summary>Copies and validates the step results.</summary>
    /// <exception cref="ArgumentNullException">The step results are null.</exception>
    /// <exception cref="ArgumentException">The report is empty, a step repeats, or the order leaves the contract sequence.</exception>
    public SessionTeardownReport(
        IReadOnlyList<SessionTeardownStepResult> steps,
        bool lockWasHeld,
        bool processExitRequested)
    {
        ArgumentNullException.ThrowIfNull(steps);

        if (steps.Count == 0)
        {
            throw new ArgumentException("A teardown report needs at least one step result.", nameof(steps));
        }

        SessionTeardownStepResult[] copy = new SessionTeardownStepResult[steps.Count];
        bool anyFailed = false;
        int previousRank = -1;
        for (int index = 0; index < steps.Count; index++)
        {
            SessionTeardownStepResult result = steps[index]
                ?? throw new ArgumentException("A teardown report cannot contain a null step result.", nameof(steps));

            // The shutdown step values are the contract order, so a strictly increasing value is the
            // same rule the plan itself enforces.
            int rank = (int)result.Step;
            if (rank <= previousRank)
            {
                throw new ArgumentException(
                    $"The teardown result for '{result.Step}' is out of plan order or repeated.",
                    nameof(steps));
            }

            previousRank = rank;
            copy[index] = result;
            anyFailed |= !result.Succeeded;
        }

        Steps = Array.AsReadOnly(copy);
        LockWasHeld = lockWasHeld;
        ProcessExitRequested = processExitRequested;
        AnyStepFailed = anyFailed;
    }

    /// <summary>One result per planned step, in plan order.</summary>
    public IReadOnlyList<SessionTeardownStepResult> Steps { get; }

    /// <summary>Whether the context said this process held the single-instance lock.</summary>
    public bool LockWasHeld { get; }

    /// <summary>Whether the context said the caller would exit the process after the teardown.</summary>
    public bool ProcessExitRequested { get; }

    /// <summary>Whether at least one step failed. A failure is never reported as a clean shutdown.</summary>
    public bool AnyStepFailed { get; }

    /// <summary>The process exit code the caller must use.</summary>
    public int ExitCode => SessionShutdown.ExitCodeFor(AnyStepFailed);
}
