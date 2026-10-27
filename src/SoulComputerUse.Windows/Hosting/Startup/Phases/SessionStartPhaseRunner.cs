using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Hosting.Startup.Phases;

/// <summary>
/// Runs one session start as the three contract segments the agent's real lifetime allows: everything
/// before the connection, the connection that shows the Windows logon screen, and everything after the
/// operator has logged on and the agent is running.
/// </summary>
/// <remarks>
/// <para>
/// The split is not cosmetic. The agent is the child session's alternate shell, so no agent process
/// exists until the operator has logged on; a pipeline that attached the agent before the connection
/// would look for a pipe nobody created and fail on every production machine. The runner therefore uses
/// three ports and one fixed order, and no port can be reached by a shortcut:
/// </para>
/// <list type="number">
/// <item><see cref="ISessionStartBeforeConnect"/>: lock, record, tier, sweep, switch, alternate shell.</item>
/// <item><see cref="ISessionConnectionOpener"/>: the one connection entry point, which shows the logon screen.</item>
/// <item><see cref="ISessionStartAfterLogon"/>: agent attachment, recorded agent session, owner pipe, ready.</item>
/// </list>
/// <para>
/// A failed segment stops the run before the next one starts, which is the whole point of the order: a
/// refused before-connect must never open a window, and a connection that did not open must never look
/// for an agent. Each failure becomes a <see cref="SessionPhaseException"/> carrying a contract code and
/// the phase that failed, so no caller has to parse text to know where the run stopped.
/// </para>
/// <para>
/// An observer is a reporting channel, so its failure is recorded in <see cref="SessionPhaseReport"/> and
/// never stops a session that is otherwise ready. Nothing is swallowed: a ready run carries every failure
/// in the report, and a run that stops carries the failures that already happened on the stopping
/// exception under <see cref="ObserverFailuresDataKey"/>.
/// </para>
/// <para>
/// The ownership record this run wrote and the lock handle the before-connect segment handed over belong
/// to the caller. The runner closes neither: a successful start hands the lock handle on in
/// <see cref="SessionPhaseReport.LockHandle"/> and keeps the record, so the session owns both for its whole
/// life. A failure after the before-connect segment carries the before-connect result on the failure's
/// <see cref="Exception.Data"/> so the caller still has the handle to close.
/// </para>
/// </remarks>
public sealed class SessionStartPhaseRunner
{
    /// <summary>The <see cref="Exception.Data"/> key under which the before-connect result travels.</summary>
    public const string BeforeConnectDataKey = "SoulComputerUse.Hosting.Startup.Phases.BeforeConnectResult";

    /// <summary>
    /// The <see cref="Exception.Data"/> key under which the observer failures collected before a failure
    /// travel, so a stopping failure cannot hide a notification that never arrived.
    /// </summary>
    public const string ObserverFailuresDataKey = "SoulComputerUse.Hosting.Startup.Phases.ObserverFailures";

    private readonly ISessionStartBeforeConnect _beforeConnect;
    private readonly ISessionConnectionOpener _connectionOpener;
    private readonly ISessionStartAfterLogon _afterLogon;
    private readonly ISessionPhaseObserver _observer;

    /// <summary>Creates one runner over the three segments and the observer.</summary>
    /// <exception cref="ArgumentNullException">A port or the observer is null.</exception>
    public SessionStartPhaseRunner(
        ISessionStartBeforeConnect beforeConnect,
        ISessionConnectionOpener connectionOpener,
        ISessionStartAfterLogon afterLogon,
        ISessionPhaseObserver observer)
    {
        ArgumentNullException.ThrowIfNull(beforeConnect);
        ArgumentNullException.ThrowIfNull(connectionOpener);
        ArgumentNullException.ThrowIfNull(afterLogon);
        ArgumentNullException.ThrowIfNull(observer);

        _beforeConnect = beforeConnect;
        _connectionOpener = connectionOpener;
        _afterLogon = afterLogon;
        _observer = observer;
    }

    /// <summary>Runs the three segments in the contract order and reports the phases it entered.</summary>
    /// <param name="cancellationToken">
    /// Checked once before the first effect. A start is deliberately not cancellable part way through,
    /// because an abandoned start would leave a held lock, a written record or an enabled switch behind.
    /// </param>
    /// <returns>The phases entered in order plus the observer failures recorded on the way.</returns>
    /// <exception cref="OperationCanceledException">The token was already cancelled, so nothing ran.</exception>
    /// <exception cref="SessionPhaseException">
    /// A segment failed, the connection could not be opened, or the after-logon segment finished without a
    /// ready record. The exception names the phase and the contract code.
    /// </exception>
    public Task<SessionPhaseReport> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Run());
    }

    private SessionPhaseReport Run()
    {
        List<SessionStartPhase> phases = [];
        List<SessionPhaseFailure> failures = [];
        BeforeConnectResult beforeConnect;

        try
        {
            beforeConnect = RunBeforeConnect(phases, failures);
            RunConnectionOpener(beforeConnect, phases, failures);
            RunAfterLogon(phases, failures);
        }
        catch (SessionPhaseException error)
        {
            // A failure must not swallow an observer failure that already happened: the caller sees both
            // the code that stopped the run and every notification that did not arrive.
            if (failures.Count != 0)
            {
                error.Data[ObserverFailuresDataKey] = failures.AsReadOnly();
            }

            throw;
        }

        return new SessionPhaseReport(phases.AsReadOnly(), failures.AsReadOnly(), beforeConnect.LockHandle);
    }

    private BeforeConnectResult RunBeforeConnect(
        List<SessionStartPhase> phases,
        List<SessionPhaseFailure> failures)
    {
        BeforeConnectResult result;
        try
        {
            result = _beforeConnect.Execute()
                ?? throw new InvalidOperationException(
                    "The before-connect segment returned no result, so nothing proves that the sweep finished or the alternate shell is configured.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw Wrap(SessionStartPhase.BeforeConnect, SessionStartPhaseMessages.BeforeConnectFailed, error);
        }

        Enter(SessionStartPhase.BeforeConnect, phases, failures);
        return result;
    }

    private void RunConnectionOpener(
        BeforeConnectResult beforeConnect,
        List<SessionStartPhase> phases,
        List<SessionPhaseFailure> failures)
    {
        // The opener opens the connection and then waits for the operator's logon, so the window is
        // showing the Windows logon screen for the whole call. Reporting the awaiting-credentials phase
        // before the call is what makes the caption say so while the operator is looking at it; reporting
        // it afterwards would leave the caption on "starting" exactly when it matters, and the phase
        // boundary would arrive only once the wait had already ended.
        Enter(SessionStartPhase.AwaitingCredentials, phases, failures);

        try
        {
            _connectionOpener.Open();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The lock the before-connect segment acquired belongs to the caller, so it travels on the
            // failure instead of being closed here: a record must never outlive the lock that makes it
            // authoritative, and only the caller knows which cleanup already ran.
            SessionPhaseException failure = Wrap(SessionStartPhase.BeforeConnect, SessionStartPhaseMessages.ConnectionFailed, error);
            failure.Data[BeforeConnectDataKey] = beforeConnect;
            throw failure;
        }
    }

    private void RunAfterLogon(List<SessionStartPhase> phases, List<SessionPhaseFailure> failures)
    {
        AfterLogonResult result;
        try
        {
            result = _afterLogon.Execute()
                ?? throw new InvalidOperationException(
                    "The after-logon segment returned no result, so nothing proves that the agent is attached.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw Wrap(SessionStartPhase.AwaitingCredentials, SessionStartPhaseMessages.AfterLogonFailed, error);
        }

        Enter(SessionStartPhase.AfterLogon, phases, failures);

        if (!result.RecordReady)
        {
            // A segment that returned without a ready record has not finished the contract's last step;
            // reporting ready here would tell a caller the session serves calls when the record still
            // refuses them.
            throw new SessionPhaseException(
                SessionErrorCode.NotReady,
                SessionStartPhase.AfterLogon,
                SessionStartPhaseMessages.RecordNotReady,
                innerException: null);
        }

        Enter(SessionStartPhase.Ready, phases, failures);
    }

    /// <summary>
    /// Records that the run reached one phase boundary and tells the observer. An observer failure is
    /// recorded rather than propagated, because a reporting channel must not decide whether a session is
    /// ready.
    /// </summary>
    private void Enter(
        SessionStartPhase phase,
        List<SessionStartPhase> phases,
        List<SessionPhaseFailure> failures)
    {
        phases.Add(phase);

        try
        {
            _observer.OnPhaseChanged(phase);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failures.Add(new SessionPhaseFailure(phase, error));
        }
    }

    /// <summary>
    /// Wraps one segment failure as a phase exception, keeping the contract code a startup refusal already
    /// carries instead of flattening every refusal into one code.
    /// </summary>
    private static SessionPhaseException Wrap(SessionStartPhase phase, string message, Exception error)
    {
        if (error is SessionPhaseException phaseError)
        {
            return phaseError;
        }

        SessionErrorCode code = error is SessionStartException startError
            ? startError.Code
            : SessionErrorCode.ManualIntervention;

        return new SessionPhaseException(code, phase, message, error);
    }
}
