using System;
using System.Collections.Generic;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.SessionLifecycle;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the session lifecycle orchestration: the startup decision table from section 3
/// and the ordered teardown from section 8. Both are pure decisions, so every branch can be asserted
/// without taking the machine-wide lock or opening a child session.
/// </summary>
[TestClass]
public sealed class SessionLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AcquiredAt = new(2026, 10, 26, 11, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void AnotherProcessHoldingTheLockRefusesWithLockHeldAndANonZeroExitCode()
    {
        SessionStartupPlan plan = SessionStartup.Plan(StartupInput(
            LockAcquireOutcome.HeldElsewhere,
            OwnerRecordReadState.Absent,
            null,
            Evidence(false, false, false, SessionState.Starting),
            true));

        Assert.AreEqual(SessionStartupKind.Refuse, plan.Kind);
        Assert.AreEqual(SessionLifecycleErrorCodes.LockHeld, plan.ErrorCode);
        Assert.AreNotEqual(SessionLifecycleExitCodes.Success, plan.ExitCode, "A held lock must never look like a clean start.");
        Assert.IsFalse(plan.ReleaseLock, "This process never acquired the lock, so it has no handle to release.");
        Assert.IsFalse(plan.DeletesStaleRecord);
        StringAssert.Contains(plan.Reason, "Task Manager", "The refusal must say how to inspect the process that holds the lock.");
    }

    [TestMethod]
    public void NoOwnershipRecordProceedsNormally()
    {
        SessionStartupPlan plan = SessionStartup.Plan(StartupInput(
            LockAcquireOutcome.Acquired,
            OwnerRecordReadState.Absent,
            null,
            Evidence(false, false, false, SessionState.Starting),
            true));

        Assert.AreEqual(SessionStartupKind.Proceed, plan.Kind);
        Assert.IsNull(plan.ErrorCode);
        Assert.AreEqual(SessionLifecycleExitCodes.Success, plan.ExitCode);
        Assert.IsFalse(plan.ReleaseLock);
        Assert.IsFalse(plan.DeletesStaleRecord);
    }

    [TestMethod]
    public void AnUnparseableRecordRefusesWithStaleRecordAndKeepsTheFileForInspection()
    {
        SessionStartupPlan plan = SessionStartup.Plan(StartupInput(
            LockAcquireOutcome.Acquired,
            OwnerRecordReadState.Unusable,
            null,
            Evidence(true, false, false, SessionState.Disconnected),
            true));

        Assert.AreEqual(SessionStartupKind.Refuse, plan.Kind);
        Assert.AreEqual(SessionLifecycleErrorCodes.LockStaleRecord, plan.ErrorCode);
        Assert.AreNotEqual(SessionLifecycleExitCodes.Success, plan.ExitCode);
        Assert.IsTrue(plan.ReleaseLock, "The lock was acquired, so a refusal must release it before exiting.");
        Assert.IsFalse(plan.DeletesStaleRecord, "A record that cannot be parsed must not be deleted automatically.");
        StringAssert.Contains(plan.Reason, "inspected by hand");
    }

    [TestMethod]
    public void AUsableRecordWithAnUnusableFieldAlsoRefusesAsAStaleRecord()
    {
        SessionOwnerRecord record = new(
            "b3f1c2d4-0000-4000-8000-000000000001",
            0,
            new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero),
            3,
            false,
            SessionState.Disconnected,
            AcquiredAt);

        SessionStartupPlan plan = SessionStartup.Plan(StartupInput(
            LockAcquireOutcome.Acquired,
            OwnerRecordReadState.Usable,
            record,
            Evidence(true, false, false, SessionState.Disconnected),
            true));

        Assert.AreEqual(SessionStartupKind.Refuse, plan.Kind);
        Assert.AreEqual(SessionLifecycleErrorCodes.LockStaleRecord, plan.ErrorCode);
        Assert.IsTrue(plan.ReleaseLock);
    }

    [TestMethod]
    public void RecoverableLeftoversAreTakenOverAfterTheRecordIsDeleted()
    {
        SessionOwnerRecord record = Record(SessionState.Disconnected);

        SessionStartupPlan plan = SessionStartup.Plan(StartupInput(
            LockAcquireOutcome.Acquired,
            OwnerRecordReadState.Usable,
            record,
            Evidence(true, false, false, SessionState.Disconnected),
            true));

        Assert.AreEqual(SessionStartupKind.RecoverAndProceed, plan.Kind);
        Assert.IsNull(plan.ErrorCode);
        Assert.AreEqual(SessionLifecycleExitCodes.Success, plan.ExitCode);
        Assert.IsTrue(plan.DeletesStaleRecord, "Takeover requires deleting the leftover record before the new one is written.");
        Assert.IsFalse(plan.ReleaseLock, "A successful takeover keeps the lock.");
    }

    [TestMethod]
    public void LeftoversThatNeedManualInterventionRefuseInsteadOfBeingDeleted()
    {
        SessionOwnerRecord record = Record(SessionState.Ready);

        SessionStartupPlan plan = SessionStartup.Plan(StartupInput(
            LockAcquireOutcome.Acquired,
            OwnerRecordReadState.Usable,
            record,
            Evidence(true, true, true, SessionState.Ready),
            true));

        Assert.AreEqual(SessionStartupKind.Refuse, plan.Kind);
        Assert.AreEqual(SessionLifecycleErrorCodes.LockStaleRecord, plan.ErrorCode);
        Assert.IsTrue(plan.ReleaseLock);
        Assert.IsFalse(plan.DeletesStaleRecord);
        StringAssert.Contains(plan.Reason, "human");
    }

    [TestMethod]
    public void TheStartupTimestampControlsTheTwentyFourHourStalenessRule()
    {
        DateTimeOffset oldAcquiredAt = Now.AddHours(-25);
        SessionOwnerRecord record = new(
            "b3f1c2d4-0000-4000-8000-000000000001",
            4242,
            new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero),
            3,
            false,
            SessionState.Ready,
            oldAcquiredAt);
        RecoveryEvidence evidence = new(
            true,
            false,
            false,
            SessionState.Ready,
            oldAcquiredAt,
            oldAcquiredAt.AddHours(1));

        SessionStartupPlan plan = SessionStartup.Plan(new SessionStartupInput(
            LockAcquireOutcome.Acquired,
            OwnerRecordReadState.Usable,
            record,
            evidence,
            true,
            Now));

        Assert.AreEqual(
            SessionStartupKind.RecoverAndProceed,
            plan.Kind,
            "The startup timestamp must drive the 24-hour rule, not a different clock read carried by the evidence.");
    }

    [TestMethod]
    public void ARecordWriteFailureRefusesAndMarksTheLockForRelease()
    {
        SessionStartupPlan plan = SessionStartup.Plan(StartupInput(
            LockAcquireOutcome.Acquired,
            OwnerRecordReadState.Absent,
            null,
            Evidence(false, false, false, SessionState.Starting),
            false));

        Assert.AreEqual(SessionStartupKind.Refuse, plan.Kind);
        Assert.AreEqual(SessionLifecycleErrorCodes.OwnerRecordWriteFailed, plan.ErrorCode);
        Assert.AreNotEqual(SessionLifecycleExitCodes.Success, plan.ExitCode, "A process without a written owner record must not exit cleanly.");
        Assert.IsTrue(plan.ReleaseLock, "The acquired lock must be closed immediately when the record cannot be written.");
        Assert.IsFalse(plan.DeletesStaleRecord);
    }

    [TestMethod]
    public void NullInputsAndInconsistentEvidenceAreRefusedAsErrors()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => SessionStartup.Plan(null!));

        Assert.ThrowsExactly<ArgumentException>(() => SessionStartup.Plan(new SessionStartupInput(
            LockAcquireOutcome.Acquired,
            OwnerRecordReadState.Absent,
            null,
            null!,
            true,
            Now)));

        Assert.ThrowsExactly<ArgumentException>(() => SessionStartup.Plan(new SessionStartupInput(
            LockAcquireOutcome.Acquired,
            OwnerRecordReadState.Absent,
            null,
            Evidence(true, false, false, SessionState.Starting),
            true,
            Now)));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SessionStartup.Plan(new SessionStartupInput(
            LockAcquireOutcome.Acquired,
            OwnerRecordReadState.Absent,
            null,
            Evidence(false, false, false, SessionState.Starting),
            true,
            default)));

        SessionOwnerRecord record = Record(SessionState.Disconnected);
        Assert.ThrowsExactly<ArgumentException>(() => SessionStartup.Plan(new SessionStartupInput(
            LockAcquireOutcome.Acquired,
            OwnerRecordReadState.Usable,
            record,
            Evidence(true, false, false, SessionState.Ready),
            true,
            Now)));
    }

    [TestMethod]
    public void AStartupPlanCannotCarryANegativeExitCodeOrHideARefusalBehindZero()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SessionStartupPlan(
            SessionStartupKind.Refuse,
            SessionLifecycleErrorCodes.LockHeld,
            -1,
            "Refused.",
            false));

        Assert.ThrowsExactly<ArgumentException>(() => new SessionStartupPlan(
            SessionStartupKind.Refuse,
            SessionLifecycleErrorCodes.LockHeld,
            SessionLifecycleExitCodes.Success,
            "Refused.",
            true));

        Assert.ThrowsExactly<ArgumentException>(() => new SessionStartupPlan(
            SessionStartupKind.Proceed,
            null,
            SessionLifecycleExitCodes.Success,
            "  ",
            false));
    }

    [TestMethod]
    public void ShutdownWithoutAChildSessionOrOurSwitchIsClosingThenReleaseThenExit()
    {
        SessionShutdownPlan plan = SessionShutdown.Plan(new SessionShutdownRequest(
            ChildSessionsOpenedByUs: false,
            ChildSessionNeedsLogOff: false,
            LockIsHeld: true));

        CollectionAssert.AreEqual(
            new List<SessionShutdownStep>
            {
                SessionShutdownStep.EnterClosing,
                SessionShutdownStep.ReleaseLock,
                SessionShutdownStep.Exit,
            },
            new List<SessionShutdownStep>(plan.Steps));
    }

    [TestMethod]
    public void AChildSessionIsLoggedOffBeforeTheSwitchAndTheLockAreTouched()
    {
        SessionShutdownPlan plan = SessionShutdown.Plan(new SessionShutdownRequest(
            ChildSessionsOpenedByUs: true,
            ChildSessionNeedsLogOff: true,
            LockIsHeld: true));

        List<SessionShutdownStep> steps = new(plan.Steps);
        CollectionAssert.AreEqual(
            new List<SessionShutdownStep>
            {
                SessionShutdownStep.EnterClosing,
                SessionShutdownStep.LogOffChildSession,
                SessionShutdownStep.RestoreChildSessionsSwitch,
                SessionShutdownStep.ReleaseLock,
                SessionShutdownStep.Exit,
            },
            steps);
    }

    [TestMethod]
    public void AChildSessionNotOpenedByUsIsLoggedOffButTheSwitchIsNotRestored()
    {
        SessionShutdownPlan plan = SessionShutdown.Plan(new SessionShutdownRequest(
            ChildSessionsOpenedByUs: false,
            ChildSessionNeedsLogOff: true,
            LockIsHeld: true));

        List<SessionShutdownStep> steps = new(plan.Steps);
        CollectionAssert.AreEqual(
            new List<SessionShutdownStep>
            {
                SessionShutdownStep.EnterClosing,
                SessionShutdownStep.LogOffChildSession,
                SessionShutdownStep.ReleaseLock,
                SessionShutdownStep.Exit,
            },
            steps);
        Assert.DoesNotContain(SessionShutdownStep.RestoreChildSessionsSwitch, steps);
    }

    [TestMethod]
    public void ASwitchOpenedByUsIsRestoredEvenWhenNoChildSessionIsLeft()
    {
        SessionShutdownPlan plan = SessionShutdown.Plan(new SessionShutdownRequest(
            ChildSessionsOpenedByUs: true,
            ChildSessionNeedsLogOff: false,
            LockIsHeld: true));

        List<SessionShutdownStep> steps = new(plan.Steps);
        CollectionAssert.AreEqual(
            new List<SessionShutdownStep>
            {
                SessionShutdownStep.EnterClosing,
                SessionShutdownStep.RestoreChildSessionsSwitch,
                SessionShutdownStep.ReleaseLock,
                SessionShutdownStep.Exit,
            },
            steps);
    }

    [TestMethod]
    public void AnyFailedStepMakesTheShutdownExitCodeNonZero()
    {
        Assert.AreEqual(SessionLifecycleExitCodes.Success, SessionShutdown.ExitCodeFor(false));
        Assert.AreNotEqual(
            SessionLifecycleExitCodes.Success,
            SessionShutdown.ExitCodeFor(true),
            "A failure must never be reported as a clean exit.");
    }

    [TestMethod]
    public void ShutdownRefusesAnOwnerThatDoesNotHoldTheLock()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => SessionShutdown.Plan(null!));

        Assert.ThrowsExactly<InvalidOperationException>(() => SessionShutdown.Plan(new SessionShutdownRequest(
            ChildSessionsOpenedByUs: false,
            ChildSessionNeedsLogOff: false,
            LockIsHeld: false)));
    }

    [TestMethod]
    public void AShutdownPlanCannotBeBuiltFromNullOrOutOfOrderSteps()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new SessionShutdownPlan(null!));

        Assert.ThrowsExactly<ArgumentException>(() => new SessionShutdownPlan(new List<SessionShutdownStep>()));

        Assert.ThrowsExactly<ArgumentException>(() => new SessionShutdownPlan(new List<SessionShutdownStep>
        {
            SessionShutdownStep.EnterClosing,
            SessionShutdownStep.ReleaseLock,
            SessionShutdownStep.LogOffChildSession,
            SessionShutdownStep.Exit,
        }));

        Assert.ThrowsExactly<ArgumentException>(() => new SessionShutdownPlan(new List<SessionShutdownStep>
        {
            SessionShutdownStep.EnterClosing,
            SessionShutdownStep.Exit,
        }));
    }

    private static SessionStartupInput StartupInput(
        LockAcquireOutcome lockOutcome,
        OwnerRecordReadState recordState,
        SessionOwnerRecord? record,
        RecoveryEvidence evidence,
        bool ownerRecordWriteSucceeded)
    {
        return new SessionStartupInput(
            lockOutcome,
            recordState,
            record,
            evidence,
            ownerRecordWriteSucceeded,
            Now);
    }

    private static SessionOwnerRecord Record(SessionState state)
    {
        return new SessionOwnerRecord(
            "b3f1c2d4-0000-4000-8000-000000000001",
            4242,
            new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero),
            3,
            false,
            state,
            AcquiredAt);
    }

    private static RecoveryEvidence Evidence(
        bool recordExists,
        bool ownerProcessAlive,
        bool ownerProcessStartTimeMatches,
        SessionState recordedState)
    {
        return new RecoveryEvidence(
            recordExists,
            ownerProcessAlive,
            ownerProcessStartTimeMatches,
            recordedState,
            AcquiredAt,
            Now);
    }
}
