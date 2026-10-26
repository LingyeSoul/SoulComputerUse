using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Windows.SessionLifecycle;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>
/// Carries out a <see cref="SessionShutdownPlan"/> in the order the plan fixes: enter <c>closing</c>,
/// log the child session off, restore the child-session switch, release the lock, then let the caller
/// exit.
/// </summary>
/// <remarks>
/// The plan and the ports are separate on purpose: the plan decides what has to happen and this type
/// decides nothing, so a step can never run out of order and a plan that contradicts the session's own
/// facts is refused before the first effect.
/// <para>
/// A failed step never stops the sequence. Releasing the lock is attempted even when the logoff failed,
/// because a process that keeps the machine-wide lock after a failed shutdown blocks every later
/// session. The report keeps one result per step, and <see cref="SessionTeardownReport.ExitCode"/> is
/// non-zero as soon as any step failed, so leftovers can never be reported as a clean exit.
/// </para>
/// <para>
/// This type never exits the process: the <see cref="SessionShutdownStep.Exit"/> step is the caller's,
/// and its result is recorded as not executed. It also writes no log entries and its report carries
/// enumerated reasons only, never free text, a path, an account identifier or a station name.
/// </para>
/// </remarks>
public sealed class SessionTeardownExecutor
{
    private readonly ISessionStateTransition _stateTransition;
    private readonly IChildSessionLogOff _childSessionLogOff;
    private readonly IChildSessionsSwitch _childSessionsSwitch;
    private readonly ISingleInstanceLockRelease _lockRelease;

    /// <summary>Creates an executor over the four effects a shutdown has.</summary>
    /// <exception cref="ArgumentNullException">A port is null.</exception>
    public SessionTeardownExecutor(
        ISessionStateTransition stateTransition,
        IChildSessionLogOff childSessionLogOff,
        IChildSessionsSwitch childSessionsSwitch,
        ISingleInstanceLockRelease lockRelease)
    {
        ArgumentNullException.ThrowIfNull(stateTransition);
        ArgumentNullException.ThrowIfNull(childSessionLogOff);
        ArgumentNullException.ThrowIfNull(childSessionsSwitch);
        ArgumentNullException.ThrowIfNull(lockRelease);

        _stateTransition = stateTransition;
        _childSessionLogOff = childSessionLogOff;
        _childSessionsSwitch = childSessionsSwitch;
        _lockRelease = lockRelease;
    }

    /// <summary>Executes every planned step and reports one result per step.</summary>
    /// <param name="plan">The ordered plan built by <see cref="SessionShutdown.Plan"/>.</param>
    /// <param name="context">This session's own facts, which a plan may contradict.</param>
    /// <param name="cancellationToken">
    /// Checked once before the first effect. A shutdown sequence is deliberately not cancellable part
    /// way through, because a cancelled teardown would leave a logged-on child session or a held lock.
    /// </param>
    /// <returns>The full report, including a non-zero exit code when any step failed.</returns>
    /// <exception cref="ArgumentNullException">The plan or the context is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The plan restores the child-session switch although this session did not turn it on.
    /// </exception>
    /// <exception cref="OperationCanceledException">The token was already cancelled, so nothing ran.</exception>
    public Task<SessionTeardownReport> ExecuteAsync(
        SessionShutdownPlan plan,
        SessionTeardownContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        EnsurePlanMatchesContext(plan, context);

        List<SessionTeardownStepResult> results = new(plan.Steps.Count);
        foreach (SessionShutdownStep step in plan.Steps)
        {
            results.Add(ExecuteStep(step, context));
        }

        return Task.FromResult(new SessionTeardownReport(
            results.AsReadOnly(),
            context.LockIsHeld,
            context.ProcessExitRequested));
    }

    /// <summary>
    /// Refuses a plan that contradicts the context before anything runs. A restore step without the
    /// switch having been opened by this session is a caller bug, not a step failure, so it throws
    /// instead of being recorded.
    /// </summary>
    private static void EnsurePlanMatchesContext(SessionShutdownPlan plan, SessionTeardownContext context)
    {
        foreach (SessionShutdownStep step in plan.Steps)
        {
            if (step == SessionShutdownStep.RestoreChildSessionsSwitch && !context.ChildSessionsOpenedByUs)
            {
                throw new InvalidOperationException(
                    "The shutdown plan restores the machine-wide child-session switch, but this session did not turn it on. Restoring it anyway would change machine state this session never touched, so the plan is refused before any step runs.");
            }
        }
    }

    /// <summary>Runs one step and maps its outcome onto an enumerated result. Never throws for a failed step.</summary>
    private SessionTeardownStepResult ExecuteStep(SessionShutdownStep step, SessionTeardownContext context)
    {
        return step switch
        {
            SessionShutdownStep.EnterClosing => ExecuteEnterClosing(),
            SessionShutdownStep.LogOffChildSession => ExecuteLogOff(context),
            SessionShutdownStep.RestoreChildSessionsSwitch => ExecuteRestore(),
            SessionShutdownStep.ReleaseLock => ExecuteReleaseLock(),
            SessionShutdownStep.Exit => new SessionTeardownStepResult(
                SessionShutdownStep.Exit,
                executed: false,
                succeeded: true,
                TeardownFailureReason.None),
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Unknown shutdown step."),
        };
    }

    private SessionTeardownStepResult ExecuteEnterClosing()
    {
        try
        {
            _stateTransition.EnterClosing();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Failed(SessionShutdownStep.EnterClosing, TeardownFailureReason.StateTransitionFailed);
        }

        return Succeeded(SessionShutdownStep.EnterClosing);
    }

    /// <summary>
    /// Logs the child session off. A plan that asks for a logoff without a positive session id fails
    /// with <see cref="TeardownFailureReason.MissingChildSession"/> and never calls the port: signing off
    /// an unknown session is not an option, and skipping the step silently would leave a child session
    /// that outlives the session that owns it.
    /// </summary>
    private SessionTeardownStepResult ExecuteLogOff(SessionTeardownContext context)
    {
        if (context.ChildWtsSessionId is not int childSessionId || childSessionId <= 0)
        {
            return new SessionTeardownStepResult(
                SessionShutdownStep.LogOffChildSession,
                executed: false,
                succeeded: false,
                TeardownFailureReason.MissingChildSession);
        }

        try
        {
            _childSessionLogOff.LogOff(childSessionId);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Failed(SessionShutdownStep.LogOffChildSession, TeardownFailureReason.LogOffFailed);
        }

        return Succeeded(SessionShutdownStep.LogOffChildSession);
    }

    private SessionTeardownStepResult ExecuteRestore()
    {
        try
        {
            _childSessionsSwitch.Restore();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Failed(SessionShutdownStep.RestoreChildSessionsSwitch, TeardownFailureReason.RestoreFailed);
        }

        return Succeeded(SessionShutdownStep.RestoreChildSessionsSwitch);
    }

    private SessionTeardownStepResult ExecuteReleaseLock()
    {
        try
        {
            _lockRelease.Release();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Failed(SessionShutdownStep.ReleaseLock, TeardownFailureReason.ReleaseFailed);
        }

        return Succeeded(SessionShutdownStep.ReleaseLock);
    }

    private static SessionTeardownStepResult Succeeded(SessionShutdownStep step)
    {
        return new SessionTeardownStepResult(step, executed: true, succeeded: true, TeardownFailureReason.None);
    }

    private static SessionTeardownStepResult Failed(SessionShutdownStep step, TeardownFailureReason reason)
    {
        return new SessionTeardownStepResult(step, executed: true, succeeded: false, reason);
    }
}
