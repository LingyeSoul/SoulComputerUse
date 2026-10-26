using System;
using SoulComputerUse.Windows.SessionLifecycle;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>The outcome of one planned teardown step.</summary>
/// <remarks>
/// <see cref="Executed"/> is false for two different situations, both visible through
/// <see cref="Failure"/>: the exit step, which the caller performs, and a logoff step whose context had
/// no child session id. A step that was not executed can still have failed; it can never have succeeded
/// without having run, except for the exit step, which this layer deliberately leaves to the caller.
/// </remarks>
public sealed record SessionTeardownStepResult
{
    /// <summary>Creates one step result and rejects a result that contradicts itself.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The step or the failure reason is not a defined value.</exception>
    /// <exception cref="ArgumentException">
    /// The failure reason does not belong to the step, or it contradicts <paramref name="succeeded"/>.
    /// </exception>
    public SessionTeardownStepResult(
        SessionShutdownStep step,
        bool executed,
        bool succeeded,
        TeardownFailureReason failure)
    {
        if (!Enum.IsDefined(step))
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "Unknown shutdown step.");
        }

        if (!Enum.IsDefined(failure))
        {
            throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown teardown failure reason.");
        }

        if (succeeded != (failure == TeardownFailureReason.None))
        {
            throw new ArgumentException(
                "A teardown step succeeds exactly when it carries no failure reason.",
                nameof(failure));
        }

        if (!IsAllowedReason(step, failure))
        {
            throw new ArgumentException(
                $"The failure reason '{failure}' cannot belong to the shutdown step '{step}'.",
                nameof(failure));
        }

        Step = step;
        Executed = executed;
        Succeeded = succeeded;
        Failure = failure;
    }

    /// <summary>The step this result belongs to.</summary>
    public SessionShutdownStep Step { get; }

    /// <summary>
    /// Whether this layer performed the step. The exit step is always false because the caller exits the
    /// process; a logoff step is false when the context carries no child session id.
    /// </summary>
    public bool Executed { get; }

    /// <summary>Whether the step's effect is in place.</summary>
    public bool Succeeded { get; }

    /// <summary>The enumerated reason; <see cref="TeardownFailureReason.None"/> exactly when the step succeeded.</summary>
    public TeardownFailureReason Failure { get; }

    private static bool IsAllowedReason(SessionShutdownStep step, TeardownFailureReason failure)
    {
        return step switch
        {
            SessionShutdownStep.EnterClosing => failure is TeardownFailureReason.None
                or TeardownFailureReason.StateTransitionFailed,
            SessionShutdownStep.LogOffChildSession => failure is TeardownFailureReason.None
                or TeardownFailureReason.LogOffFailed
                or TeardownFailureReason.MissingChildSession,
            SessionShutdownStep.RestoreChildSessionsSwitch => failure is TeardownFailureReason.None
                or TeardownFailureReason.RestoreFailed,
            SessionShutdownStep.ReleaseLock => failure is TeardownFailureReason.None
                or TeardownFailureReason.ReleaseFailed,
            SessionShutdownStep.Exit => failure == TeardownFailureReason.None,
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Unknown shutdown step."),
        };
    }
}
