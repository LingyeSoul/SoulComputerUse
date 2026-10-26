using System;
using System.Collections.Generic;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.ChildSessions;
using SoulComputerUse.Windows.Hosting.Startup.Phases;
using SoulComputerUse.Windows.Rdp.Launch;
using SoulComputerUse.Windows.Rdp.SessionLaunch;
using SoulComputerUse.Windows.SessionLifecycle.Teardown;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.Windows.Hosting.Startup.Split;

/// <summary>
/// Everything a child-session start has to settle before anything may connect: the machine-wide lock,
/// the ownership record in <c>starting</c>, the credential tier, the leftover sweep, the child-session
/// switch and the alternate-shell properties that make the session run the agent.
/// </summary>
/// <remarks>
/// <para>
/// This segment never connects. Opening the child session belongs to <see cref="ISessionConnectionOpener"/>,
/// which the caller runs only after this segment returns; a start that connected here would open a window
/// before the sweep and the switch were settled, and it would be the second connection path the contract
/// forbids.
/// </para>
/// <para>
/// The order is the contract order of sections 3, 3.1.1 and 7.2: lock, record, <c>starting</c>, tier,
/// sweep, switch, marker, alternate shell. Each gate refuses as soon as it is judged, so a <c>none</c>
/// tier refuses before anything is swept, and a failed sweep refuses before the switch is read. The
/// refusals reuse <see cref="SessionStartOrchestrator"/>'s gate functions, so no rule and no explanation
/// is stated twice.
/// </para>
/// <para>
/// A failed step cleans up unconditionally: the record this attempt wrote is deleted and the lock handle
/// is closed, in that order, because a record must never outlive the lock that makes it authoritative. A
/// record this attempt never wrote stays on disk, since it is the only evidence of the previous owner.
/// The alternate-shell properties are in-memory connection settings, so there is nothing else to release.
/// </para>
/// <para>
/// The alternate-shell properties are written on the launch surface the caller supplies, which production
/// builds from the same control <see cref="IRdpControlSurface"/> exposes. The surface is injected instead of
/// being built here from the control object: building <see cref="DynamicRdpLaunchSurface"/> here would hide
/// the ActiveX dependency inside the segment and make it impossible to run the segment without a control,
/// while an injected surface keeps the write verifiable and the segment testable. Reading the control before
/// the write still proves the ActiveX host attached one, so an injected surface cannot make the segment
/// report "configured" for a session that has no control at all.
/// </para>
/// <para>
/// Every explanation is a fixed template plus an enumerated value or a count, so no failure carries a
/// path, an account, a SID or a nonce. This type writes no log entries.
/// </para>
/// </remarks>
public sealed class BeforeConnectSegment : ISessionStartBeforeConnect
{
    private readonly SessionStartOptions _options;
    private readonly ISingleInstanceLockAcquire _lockAcquire;
    private readonly IOwnerRecordStore _recordStore;
    private readonly IAuthTierSource _authTierSource;
    private readonly ILeftoverSweep _leftoverSweep;
    private readonly IChildSessionsGate _childSessionsGate;
    private readonly AgentLaunchRequest _launchRequest;
    private readonly IRdpControlSurface _controlSurface;
    private readonly IRdpLaunchSurface _launchSurface;
    private readonly SplitStepLog _steps = new();

    private bool _ran;
    private bool _recordWrittenByThisAttempt;

    /// <summary>Creates one before-connect segment over the ports and the launch values a start needs.</summary>
    /// <exception cref="ArgumentNullException">The options, a port or the launch request is null.</exception>
    public BeforeConnectSegment(
        SessionStartOptions options,
        ISingleInstanceLockAcquire lockAcquire,
        IOwnerRecordStore recordStore,
        IAuthTierSource authTierSource,
        ILeftoverSweep leftoverSweep,
        IChildSessionsGate childSessionsGate,
        AgentLaunchRequest launchRequest,
        IRdpControlSurface controlSurface,
        IRdpLaunchSurface launchSurface)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(lockAcquire);
        ArgumentNullException.ThrowIfNull(recordStore);
        ArgumentNullException.ThrowIfNull(authTierSource);
        ArgumentNullException.ThrowIfNull(leftoverSweep);
        ArgumentNullException.ThrowIfNull(childSessionsGate);
        ArgumentNullException.ThrowIfNull(launchRequest);
        ArgumentNullException.ThrowIfNull(controlSurface);
        ArgumentNullException.ThrowIfNull(launchSurface);

        _options = options;
        _lockAcquire = lockAcquire;
        _recordStore = recordStore;
        _authTierSource = authTierSource;
        _leftoverSweep = leftoverSweep;
        _childSessionsGate = childSessionsGate;
        _launchRequest = launchRequest;
        _controlSurface = controlSurface;
        _launchSurface = launchSurface;
    }

    /// <summary>
    /// The credential tier this segment probed, or null while the tier stage has not run. A successful
    /// segment always reports a tier, because the <c>none</c> tier refuses the start.
    /// </summary>
    public AuthTier? AuthTier { get; private set; }

    /// <summary>The stages this segment has executed so far, in contract order.</summary>
    public IReadOnlyList<SessionStartStepResult> SessionStartSteps => _steps.Snapshot;

    /// <summary>
    /// Takes the lock, settles every pre-connection gate and writes the alternate-shell properties.
    /// </summary>
    /// <returns>The completed segment, including the lock handle the running session keeps.</returns>
    /// <exception cref="InvalidOperationException">The segment was already run, or the lock port returned no handle.</exception>
    /// <exception cref="SessionStartException">
    /// A gate refused the start or a step failed. The exception carries the contract code and the stages
    /// that ran, and everything this attempt wrote has already been cleaned up.
    /// </exception>
    public BeforeConnectResult Execute()
    {
        if (_ran)
        {
            throw new InvalidOperationException(SessionStartSplitMessages.SegmentAlreadyRan);
        }

        _ran = true;

        IDisposable? lockHandle = null;
        try
        {
            lockHandle = TakeLock();
            OwnerRecordStatus recordStatus = ReadOwnerRecord();
            if (recordStatus == OwnerRecordStatus.TakeOver)
            {
                DeleteLeftoverRecord();
            }

            // The record is written before the credential gate on purpose: a start that is refused later
            // still left evidence of which process held the lock, and the record is what the next start
            // judges. Every failure below deletes it again.
            WriteStartingRecord(childSessionsOpenedByUs: false, SessionStartStage.WriteStartingRecord);
            ProbeAuthTier();
            SweepLeftovers();
            bool childSessionsOpenedByUs = SettleChildSessions();
            ConfigureAlternateShell();

            return new BeforeConnectResult(
                sweepCompleted: true,
                childSessionsOpenedByUs: childSessionsOpenedByUs,
                alternateShellConfigured: true,
                lockHandle: lockHandle);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw Cleanup(error, lockHandle);
        }
    }

    private IDisposable TakeLock()
    {
        LockAttempt attempt;
        try
        {
            attempt = _lockAcquire.Acquire();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new SessionStartException(
                SessionErrorCode.ManualIntervention,
                SessionStartMessages.LockProbeFailed,
                _steps.Snapshot,
                error);
        }

        SessionStartRefusal? refusal = SessionStartOrchestrator.EvaluateLock(attempt.Outcome);
        if (refusal is not null)
        {
            throw new SessionStartException(refusal.Code, refusal.Reason, _steps.Snapshot, innerException: null);
        }

        IDisposable handle = attempt.Handle
            ?? throw new InvalidOperationException(
                "The lock port reported that this process acquired the lock without returning the handle that keeps it alive.");

        _steps.Succeeded(SessionStartStage.Lock);
        return handle;
    }

    private OwnerRecordStatus ReadOwnerRecord()
    {
        OwnerRecordRead read;
        try
        {
            read = _recordStore.Read();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw StageFailure(
                SessionStartStage.ReadOwnerRecord,
                SessionErrorCode.LockStaleRecord,
                SessionStartMessages.RecordReadFailed,
                error);
        }

        SessionStartRefusal? refusal = SessionStartOrchestrator.EvaluateRecord(read.Status);
        if (refusal is not null)
        {
            _steps.Failed(SessionStartStage.ReadOwnerRecord, refusal.Code);
            throw new SessionStartException(refusal.Code, refusal.Reason, _steps.Snapshot, innerException: null);
        }

        _steps.Succeeded(SessionStartStage.ReadOwnerRecord);
        return read.Status;
    }

    private void DeleteLeftoverRecord()
    {
        try
        {
            _recordStore.Delete();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw StageFailure(
                SessionStartStage.TakeOverRecord,
                SessionErrorCode.LockStaleRecord,
                SessionStartMessages.RecordReadFailed,
                error);
        }

        _steps.Succeeded(SessionStartStage.TakeOverRecord);
    }

    /// <summary>
    /// Writes the ownership record in <c>starting</c>. The flag is set before the write because a write
    /// that failed part way may still have left a file, and this attempt owns the lock either way.
    /// </summary>
    private void WriteStartingRecord(bool childSessionsOpenedByUs, SessionStartStage stage)
    {
        _recordWrittenByThisAttempt = true;
        try
        {
            _recordStore.Write(new SessionOwnerRecord(
                _options.SessionId,
                _options.HostPid,
                _options.HostProcessStartTime,
                0,
                childSessionsOpenedByUs,
                SessionState.Starting,
                _options.AcquiredAt));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw StageFailure(
                stage,
                SessionErrorCode.ManualIntervention,
                SessionStartMessages.RecordWriteFailed,
                error);
        }

        _steps.Succeeded(stage);
    }

    /// <summary>
    /// Rewrites the same <c>starting</c> record with the marker that this session enabled the machine-wide
    /// switch, so shutdown turns it off again and nobody else's setting is touched.
    /// </summary>
    private void MarkChildSessionsOpenedByUs()
    {
        WriteStartingRecord(childSessionsOpenedByUs: true, SessionStartStage.RecordChildSessions);
    }

    private void ProbeAuthTier()
    {
        AuthTier tier;
        try
        {
            tier = _authTierSource.Detect();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw StageFailure(
                SessionStartStage.AuthTier,
                SessionErrorCode.ManualIntervention,
                SessionStartMessages.AuthTierProbeFailed,
                error);
        }

        SessionStartRefusal? refusal = SessionStartOrchestrator.EvaluateAuthTier(tier);
        if (refusal is not null)
        {
            _steps.Failed(SessionStartStage.AuthTier, refusal.Code);
            throw new SessionStartException(refusal.Code, refusal.Reason, _steps.Snapshot, innerException: null);
        }

        AuthTier = tier;
        _steps.Succeeded(SessionStartStage.AuthTier);
    }

    private void SweepLeftovers()
    {
        LeftoverSweepOutcome outcome;
        Exception? sweepError = null;
        try
        {
            LeftoverSweepReport report = _leftoverSweep.Sweep();
            outcome = report.RemainingSessionIds.Count == 0
                ? LeftoverSweepOutcome.Completed(report)
                : LeftoverSweepOutcome.Failed(report);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The sweep's own report travels on the exception, so the refusal can still say how far the
            // attempt got. The exception stays available as the refusal's inner failure.
            sweepError = error;
            outcome = LeftoverSweepOutcome.Failed(error.Data[LeftoverSweeper.ReportDataKey] as LeftoverSweepReport);
        }

        SessionStartRefusal? refusal = SessionStartOrchestrator.EvaluateSweep(outcome);
        if (refusal is not null)
        {
            _steps.Failed(SessionStartStage.Sweep, refusal.Code);
            throw new SessionStartException(refusal.Code, refusal.Reason, _steps.Snapshot, sweepError);
        }

        _steps.Succeeded(SessionStartStage.Sweep);
    }

    /// <summary>
    /// Probes the switch and both policy values, asks for elevation when the machine allows it, and
    /// rewrites the record when this process turned the switch on.
    /// </summary>
    /// <returns>Whether this session enabled the machine-wide child-session switch.</returns>
    private bool SettleChildSessions()
    {
        bool enabled;
        try
        {
            enabled = _childSessionsGate.IsEnabled();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw StageFailure(
                SessionStartStage.ChildSessions,
                SessionErrorCode.ManualIntervention,
                SessionStartMessages.ChildSessionsProbeFailed,
                error);
        }

        PolicyState rdpInbound = ReadPolicy(PolicyValue.RdpInbound);
        PolicyState childConnections = ReadPolicy(PolicyValue.ChildConnections);

        ChildSessionsDecision decision = ChildSessionsPolicy.Evaluate(enabled, rdpInbound, childConnections);
        SessionStartRefusal? refusal = SessionStartOrchestrator.EvaluateChildSessions(decision);
        if (refusal is not null)
        {
            _steps.Failed(SessionStartStage.ChildSessions, refusal.Code);
            throw new SessionStartException(refusal.Code, refusal.Reason, _steps.Snapshot, innerException: null);
        }

        _steps.Succeeded(SessionStartStage.ChildSessions);

        if (decision.Kind != ChildSessionsDecisionKind.RequestElevation)
        {
            return false;
        }

        EnableChildSessions();
        MarkChildSessionsOpenedByUs();
        return true;
    }

    private void EnableChildSessions()
    {
        ElevationResult result;
        try
        {
            result = _childSessionsGate.Enable();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw StageFailure(
                SessionStartStage.EnableChildSessions,
                SessionErrorCode.ElevationUnavailable,
                SessionStartMessages.ElevationUnavailable,
                error);
        }

        if (result.Failed)
        {
            ElevationFailure failure = result.Failure
                ?? throw new InvalidOperationException(
                    "The elevation result reported a failure without naming the enumerated reason.");

            SessionStartRefusal refusal = SessionStartOrchestrator.EvaluateElevation(failure);
            _steps.Failed(SessionStartStage.EnableChildSessions, refusal.Code);
            throw new SessionStartException(refusal.Code, refusal.Reason, _steps.Snapshot, innerException: null);
        }

        // Success of the helper is not proof: a helper that reported success while the switch stayed off
        // would let the connection be attempted with the capability still missing. The stage therefore
        // succeeds only after the read-back agrees, so the recorded stage is never both succeeded and
        // failed, and the step order stays monotone.
        bool enabled;
        try
        {
            enabled = _childSessionsGate.IsEnabled();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw StageFailure(
                SessionStartStage.EnableChildSessions,
                SessionErrorCode.ChildSessionsDisabled,
                SessionStartMessages.ChildSessionsProbeFailed,
                error);
        }

        if (!enabled)
        {
            throw StageFailure(
                SessionStartStage.EnableChildSessions,
                SessionErrorCode.ChildSessionsDisabled,
                SessionStartMessages.ChildSessionsStayedOff,
                new InvalidOperationException(
                    "The elevation helper reported success, but reading the switch back still reports child sessions as disabled."));
        }

        _steps.Succeeded(SessionStartStage.EnableChildSessions);
    }

    private PolicyState ReadPolicy(PolicyValue value)
    {
        try
        {
            return _childSessionsGate.ReadPolicy(value);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            SessionErrorCode code = value switch
            {
                PolicyValue.RdpInbound => SessionErrorCode.RdpInboundDenied,
                PolicyValue.ChildConnections => SessionErrorCode.ChildConnectionsDenied,
                _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown policy value."),
            };

            throw StageFailure(
                SessionStartStage.ChildSessions,
                code,
                SessionStartMessages.PolicyValueMalformed(value),
                error);
        }
    }

    /// <summary>
    /// Writes the two alternate-shell properties that make the child session run the agent instead of the
    /// usual shell. The write is verified by <see cref="RdpLaunchConfigurator"/> itself.
    /// </summary>
    private void ConfigureAlternateShell()
    {
        try
        {
            _ = _controlSurface.Ocx;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new SessionStartException(
                SessionErrorCode.ManualIntervention,
                SessionStartSplitMessages.AlternateShellUnavailable,
                _steps.Snapshot,
                error);
        }

        try
        {
            RdpLaunchConfigurator.Apply(_launchSurface, _launchRequest);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new SessionStartException(
                SessionErrorCode.ManualIntervention,
                SessionStartSplitMessages.AlternateShellWriteFailed,
                _steps.Snapshot,
                error);
        }
    }

    /// <summary>Records one failed stage and builds the refusal that reports it.</summary>
    private SessionStartException StageFailure(
        SessionStartStage stage,
        SessionErrorCode code,
        string reason,
        Exception innerException)
    {
        _steps.Failed(stage, code);
        return new SessionStartException(code, reason, _steps.Snapshot, innerException);
    }

    /// <summary>
    /// Deletes the record this attempt wrote, then closes the lock handle. Every step is attempted even
    /// when an earlier one fails, and the failures travel beside the original refusal instead of being
    /// swallowed.
    /// </summary>
    private SessionStartException Cleanup(Exception error, IDisposable? lockHandle)
    {
        List<Exception> failures = [];
        if (_recordWrittenByThisAttempt)
        {
            try
            {
                _recordStore.Delete();
            }
            catch (Exception deleteError) when (deleteError is not OutOfMemoryException)
            {
                failures.Add(deleteError);
            }

            _recordWrittenByThisAttempt = false;
        }

        if (lockHandle is not null)
        {
            try
            {
                lockHandle.Dispose();
            }
            catch (Exception disposeError) when (disposeError is not OutOfMemoryException)
            {
                failures.Add(disposeError);
            }
        }

        SessionStartException reported = error as SessionStartException
            ?? new SessionStartException(
                SessionErrorCode.ManualIntervention,
                SessionStartMessages.UnexpectedStageFailure,
                _steps.Snapshot,
                error);

        if (failures.Count == 0)
        {
            return reported;
        }

        List<Exception> combined = [reported];
        combined.AddRange(failures);
        return new SessionStartException(
            reported.Code,
            reported.Message,
            reported.Steps,
            new AggregateException(SessionStartSplitMessages.BeforeConnectCleanupFailed, combined));
    }
}
