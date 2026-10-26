using System;
using System.Collections.Generic;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.ChildSessions;
using SoulComputerUse.Windows.Hosting.Startup;
using SoulComputerUse.Windows.SessionLifecycle.Teardown;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the startup decision table of sections 3, 3.1.1, 3.3 and 7.2. Every case is a pure
/// function call over one immutable facts snapshot, so no case takes the machine-wide lock, changes a
/// policy value, prompts for elevation or opens a connection.
/// </summary>
[TestClass]
public sealed class SessionStartOrchestrationTests
{
    /// <summary>A nonce-shaped value used to prove that no explanation can carry one.</summary>
    private const string Nonce = "0123456789ABCDEF0123456789ABCDEF";

    [TestMethod]
    public void AHeldLockRefusesBeforeAnyOtherGateIsJudged()
    {
        SessionStartPlan plan = SessionStartOrchestrator.Decide(new SessionStartFacts(
            LockAcquireOutcome.HeldElsewhere,
            OwnerRecordStatus.NotTakeable,
            AuthTier.None,
            sweep: null,
            childSessions: null,
            elevationFailure: null,
            agent: null));

        Assert.AreEqual(SessionStartKind.Refuse, plan.Kind);
        Assert.AreEqual(SessionErrorCode.LockHeld, plan.ErrorCode);
        Assert.AreEqual(SessionStartStep.None, plan.NextStep);
        Assert.IsFalse(plan.AllowsStart);
        Assert.IsFalse(plan.DeletesStaleRecord);
        StringAssert.Contains(plan.Reason, "Task Manager");
    }

    [TestMethod]
    public void AnOwnershipRecordThatCannotBeTakenOverRefusesAsAStaleRecord()
    {
        SessionStartPlan plan = SessionStartOrchestrator.Decide(Facts(
            recordStatus: OwnerRecordStatus.NotTakeable,
            authTier: AuthTier.HelloEnrolled,
            sweep: CompletedSweep(),
            childSessions: Allowed()));

        Assert.AreEqual(SessionStartKind.Refuse, plan.Kind);
        Assert.AreEqual(SessionErrorCode.LockStaleRecord, plan.ErrorCode);
        Assert.IsFalse(plan.DeletesStaleRecord, "A record that must be inspected by hand is never deleted.");
        StringAssert.Contains(plan.Reason, "inspected by hand");
    }

    [TestMethod]
    public void TheNoneTierRefusesWithManualInterventionAndNeverFallsBack()
    {
        SessionStartPlan plan = SessionStartOrchestrator.Decide(Facts(
            authTier: AuthTier.None,
            sweep: null,
            childSessions: null));

        Assert.AreEqual(SessionStartKind.Refuse, plan.Kind);
        Assert.AreEqual(SessionErrorCode.ManualIntervention, plan.ErrorCode);
        Assert.AreEqual(SessionStartStep.None, plan.NextStep);
        StringAssert.Contains(plan.Reason, "no usable system verification path");
    }

    [TestMethod]
    public void BothAcceptableTiersContinueToTheSweep()
    {
        foreach (AuthTier tier in new[] { AuthTier.HelloEnrolled, AuthTier.SystemCredential })
        {
            SessionStartPlan plan = SessionStartOrchestrator.Decide(Facts(authTier: tier, sweep: null, childSessions: null));

            Assert.AreEqual(SessionStartKind.Proceed, plan.Kind);
            Assert.IsNull(plan.ErrorCode);
            Assert.AreEqual(
                SessionStartStep.Sweep,
                plan.NextStep,
                "Nothing may be connected before the leftover child sessions have been swept.");
        }
    }

    [TestMethod]
    public void ATakeoverIsReportedAndStillAsksForTheSweepFirst()
    {
        SessionStartPlan plan = SessionStartOrchestrator.Decide(Facts(
            recordStatus: OwnerRecordStatus.TakeOver,
            sweep: null,
            childSessions: null));

        Assert.AreEqual(SessionStartKind.RecoverAndProceed, plan.Kind);
        Assert.IsTrue(plan.DeletesStaleRecord);
        Assert.AreEqual(SessionStartStep.Sweep, plan.NextStep);
    }

    [TestMethod]
    public void AFailedSweepRefusesWithManualIntervention()
    {
        LeftoverSweepOutcome failure = LeftoverSweepOutcome.Failed(new LeftoverSweepReport([4, 5], [5]));

        SessionStartPlan plan = SessionStartOrchestrator.Decide(Facts(sweep: failure, childSessions: null));

        Assert.AreEqual(SessionStartKind.Refuse, plan.Kind);
        Assert.AreEqual(SessionErrorCode.ManualIntervention, plan.ErrorCode);
        Assert.AreEqual(SessionStartStep.None, plan.NextStep);
        StringAssert.Contains(plan.Reason, "2 logoff request(s)");
        StringAssert.Contains(plan.Reason, "1 session(s) still present");
    }

    [TestMethod]
    public void AFailedSweepWithoutAReportStillRefusesAndReportsZeroCounts()
    {
        SessionStartPlan plan = SessionStartOrchestrator.Decide(Facts(
            sweep: LeftoverSweepOutcome.Failed(report: null),
            childSessions: null));

        Assert.AreEqual(SessionErrorCode.ManualIntervention, plan.ErrorCode);
        StringAssert.Contains(plan.Reason, "0 logoff request(s)");
        StringAssert.Contains(plan.Reason, "0 session(s) still present");
    }

    [TestMethod]
    public void ADeniedRemoteDesktopInboundValueRefusesWithRdpInboundDenied()
    {
        SessionStartPlan plan = SessionStartOrchestrator.Decide(Facts(
            sweep: CompletedSweep(),
            childSessions: Refusal(PolicyValue.RdpInbound, PolicyProblem.Denied)));

        Assert.AreEqual(SessionStartKind.Refuse, plan.Kind);
        Assert.AreEqual(SessionErrorCode.RdpInboundDenied, plan.ErrorCode);
        StringAssert.Contains(plan.Reason, "fDenyTSConnections");
    }

    [TestMethod]
    public void AMissingRemoteDesktopInboundValueRefusesInsteadOfBeingAssumedAllowed()
    {
        SessionStartPlan plan = SessionStartOrchestrator.Decide(Facts(
            sweep: CompletedSweep(),
            childSessions: Refusal(PolicyValue.RdpInbound, PolicyProblem.Missing)));

        Assert.AreEqual(SessionErrorCode.RdpInboundDenied, plan.ErrorCode);
        StringAssert.Contains(plan.Reason, "fDenyTSConnections");
    }

    [TestMethod]
    public void ADeniedChildConnectionsValueRefusesWithChildConnectionsDenied()
    {
        SessionStartPlan plan = SessionStartOrchestrator.Decide(Facts(
            sweep: CompletedSweep(),
            childSessions: Refusal(PolicyValue.ChildConnections, PolicyProblem.Denied)));

        Assert.AreEqual(SessionStartKind.Refuse, plan.Kind);
        Assert.AreEqual(SessionErrorCode.ChildConnectionsDenied, plan.ErrorCode);
        StringAssert.Contains(plan.Reason, "fDenyChildConnections");
    }

    [TestMethod]
    public void AMissingChildConnectionsValueRefusesWithChildConnectionsDenied()
    {
        SessionStartPlan plan = SessionStartOrchestrator.Decide(Facts(
            sweep: CompletedSweep(),
            childSessions: Refusal(PolicyValue.ChildConnections, PolicyProblem.Missing)));

        Assert.AreEqual(SessionErrorCode.ChildConnectionsDenied, plan.ErrorCode);
    }

    [TestMethod]
    public void ARefusedPolicyGateIsJudgedBeforeElevationIsEverAskedFor()
    {
        SessionStartFacts facts = Facts(
            sweep: CompletedSweep(),
            childSessions: Refusal(PolicyValue.RdpInbound, PolicyProblem.Denied));

        Assert.ThrowsExactly<ArgumentException>(() => new SessionStartFacts(
            LockAcquireOutcome.Acquired,
            OwnerRecordStatus.Absent,
            AuthTier.HelloEnrolled,
            facts.Sweep,
            facts.ChildSessions,
            ElevationFailure.Declined,
            agent: null));
    }

    [TestMethod]
    public void ARequestForElevationContinuesRatherThanRefusing()
    {
        SessionStartPlan plan = SessionStartOrchestrator.Decide(Facts(
            sweep: CompletedSweep(),
            childSessions: ElevationRequired()));

        Assert.AreEqual(SessionStartKind.Proceed, plan.Kind);
        Assert.IsNull(plan.ErrorCode);
        Assert.AreEqual(
            SessionStartStep.EnableChildSessions,
            plan.NextStep,
            "A machine that allows child sessions but has the switch off needs consent, not a refusal.");
        StringAssert.Contains(plan.Reason, "asks the operator");
    }

    [TestMethod]
    public void ElevationDeclinedMapsToChildSessionsDeclined()
    {
        SessionStartPlan plan = SessionStartOrchestrator.Decide(Facts(
            sweep: CompletedSweep(),
            childSessions: ElevationRequired(),
            elevationFailure: ElevationFailure.Declined));

        Assert.AreEqual(SessionStartKind.Refuse, plan.Kind);
        Assert.AreEqual(SessionErrorCode.ChildSessionsDeclined, plan.ErrorCode);
    }

    [TestMethod]
    public void ElevationUnavailableMapsToElevationUnavailable()
    {
        SessionStartPlan plan = SessionStartOrchestrator.Decide(Facts(
            sweep: CompletedSweep(),
            childSessions: ElevationRequired(),
            elevationFailure: ElevationFailure.Unavailable));

        Assert.AreEqual(SessionStartKind.Refuse, plan.Kind);
        Assert.AreEqual(SessionErrorCode.ElevationUnavailable, plan.ErrorCode);
        StringAssert.Contains(plan.Reason, "administrator");
    }

    [TestMethod]
    public void ElevationHelperFailureMapsToChildSessionsDisabled()
    {
        SessionStartPlan plan = SessionStartOrchestrator.Decide(Facts(
            sweep: CompletedSweep(),
            childSessions: ElevationRequired(),
            elevationFailure: ElevationFailure.HelperFailed));

        Assert.AreEqual(SessionStartKind.Refuse, plan.Kind);
        Assert.AreEqual(SessionErrorCode.ChildSessionsDisabled, plan.ErrorCode);
    }

    [TestMethod]
    public void AnAllowedMachineWithoutAnAgentYetAsksToConnectTheAgent()
    {
        SessionStartPlan plan = SessionStartOrchestrator.Decide(Facts(
            sweep: CompletedSweep(),
            childSessions: Allowed()));

        Assert.AreEqual(SessionStartKind.Proceed, plan.Kind);
        Assert.AreEqual(SessionStartStep.ConnectAgent, plan.NextStep);
    }

    [DataRow(SessionErrorCode.AgentTimeout)]
    [DataRow(SessionErrorCode.AgentDead)]
    [DataRow(SessionErrorCode.IpcPeer)]
    [DataRow(SessionErrorCode.IpcProtocol)]
    [DataRow(SessionErrorCode.Disconnected)]
    [DataRow(SessionErrorCode.Blocked)]
    [TestMethod]
    public void AnAgentFailurePassesItsOwnContractCodeThrough(SessionErrorCode code)
    {
        SessionStartPlan plan = SessionStartOrchestrator.Decide(Facts(
            sweep: CompletedSweep(),
            childSessions: Allowed(),
            agent: AgentAttachOutcome.Failed(code)));

        Assert.AreEqual(SessionStartKind.Refuse, plan.Kind);
        Assert.AreEqual(code, plan.ErrorCode);
        StringAssert.Contains(plan.Reason, SessionErrorCodeNames.ToWireName(code));
    }

    [TestMethod]
    public void AConnectedAgentAsksToRunTheSession()
    {
        SessionStartPlan plan = SessionStartOrchestrator.Decide(Facts(
            sweep: CompletedSweep(),
            childSessions: Allowed(),
            agent: AgentAttachOutcome.Succeeded(new AgentConnection(new UnavailableSessionClient(), 4242))));

        Assert.AreEqual(SessionStartKind.Proceed, plan.Kind);
        Assert.IsNull(plan.ErrorCode);
        Assert.AreEqual(SessionStartStep.Run, plan.NextStep);
    }

    [TestMethod]
    public void TheSameFactsAlwaysProduceTheSamePlan()
    {
        SessionStartFacts facts = Facts(
            sweep: CompletedSweep(),
            childSessions: ElevationRequired(),
            elevationFailure: ElevationFailure.Declined);

        SessionStartPlan first = SessionStartOrchestrator.Decide(facts);
        SessionStartPlan second = SessionStartOrchestrator.Decide(facts);

        Assert.AreEqual(first.Kind, second.Kind);
        Assert.AreEqual(first.ErrorCode, second.ErrorCode);
        Assert.AreEqual(first.NextStep, second.NextStep);
        Assert.AreEqual(first.Reason, second.Reason);
    }

    [TestMethod]
    public void NoExplanationCarriesAPathOrANonce()
    {
        List<SessionStartPlan> plans =
        [
            SessionStartOrchestrator.Decide(new SessionStartFacts(
                LockAcquireOutcome.HeldElsewhere,
                OwnerRecordStatus.Absent,
                AuthTier.HelloEnrolled,
                sweep: null,
                childSessions: null,
                elevationFailure: null,
                agent: null)),
            SessionStartOrchestrator.Decide(Facts(recordStatus: OwnerRecordStatus.NotTakeable)),
            SessionStartOrchestrator.Decide(Facts(authTier: AuthTier.None)),
            SessionStartOrchestrator.Decide(Facts(sweep: LeftoverSweepOutcome.Failed(new LeftoverSweepReport([2], [2])))),
            SessionStartOrchestrator.Decide(Facts(sweep: CompletedSweep(), childSessions: Refusal(PolicyValue.RdpInbound, PolicyProblem.Missing))),
            SessionStartOrchestrator.Decide(Facts(sweep: CompletedSweep(), childSessions: Refusal(PolicyValue.ChildConnections, PolicyProblem.Denied))),
            SessionStartOrchestrator.Decide(Facts(sweep: CompletedSweep(), childSessions: ElevationRequired())),
            SessionStartOrchestrator.Decide(Facts(sweep: CompletedSweep(), childSessions: ElevationRequired(), elevationFailure: ElevationFailure.Declined)),
            SessionStartOrchestrator.Decide(Facts(sweep: CompletedSweep(), childSessions: Allowed())),
            SessionStartOrchestrator.Decide(Facts(
                sweep: CompletedSweep(),
                childSessions: Allowed(),
                agent: AgentAttachOutcome.Failed(SessionErrorCode.IpcPeer))),
        ];

        foreach (SessionStartPlan plan in plans)
        {
            Assert.IsFalse(plan.Reason.Contains("C:\\", StringComparison.OrdinalIgnoreCase), plan.Reason);
            Assert.IsFalse(plan.Reason.Contains('/', StringComparison.Ordinal), plan.Reason);
            Assert.IsFalse(plan.Reason.Contains('\\', StringComparison.Ordinal), plan.Reason);
            Assert.IsFalse(plan.Reason.Contains(Nonce, StringComparison.OrdinalIgnoreCase), plan.Reason);
            Assert.IsFalse(plan.Reason.Contains("S-1-5-21", StringComparison.OrdinalIgnoreCase), plan.Reason);
        }
    }

    [TestMethod]
    public void APlanCannotCarryARefusalWithoutACodeOrANextStep()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SessionStartPlan(
            SessionStartKind.Refuse,
            errorCode: null,
            "Refused.",
            SessionStartStep.None));

        Assert.ThrowsExactly<ArgumentException>(() => new SessionStartPlan(
            SessionStartKind.Refuse,
            SessionErrorCode.LockHeld,
            "Refused.",
            SessionStartStep.Sweep));

        Assert.ThrowsExactly<ArgumentException>(() => new SessionStartPlan(
            SessionStartKind.Proceed,
            SessionErrorCode.LockHeld,
            "Go on.",
            SessionStartStep.Sweep));

        Assert.ThrowsExactly<ArgumentException>(() => new SessionStartPlan(
            SessionStartKind.Proceed,
            errorCode: null,
            "Go on.",
            SessionStartStep.None));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SessionStartPlan(
            (SessionStartKind)99,
            errorCode: null,
            "Go on.",
            SessionStartStep.Sweep));
    }

    [TestMethod]
    public void AFactsSnapshotRefusesAnAgentOutcomeWithoutTheSweepBeforeIt()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SessionStartFacts(
            LockAcquireOutcome.Acquired,
            OwnerRecordStatus.Absent,
            AuthTier.HelloEnrolled,
            sweep: null,
            childSessions: null,
            elevationFailure: null,
            agent: AgentAttachOutcome.Failed(SessionErrorCode.AgentDead)));
    }

    [TestMethod]
    public void TheDecisionNeedsFactsAndRejectsAnUnknownValue()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => SessionStartOrchestrator.Decide(null!));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SessionStartFacts(
            (LockAcquireOutcome)99,
            OwnerRecordStatus.Absent,
            AuthTier.HelloEnrolled,
            sweep: null,
            childSessions: null,
            elevationFailure: null,
            agent: null));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SessionStartFacts(
            LockAcquireOutcome.Acquired,
            (OwnerRecordStatus)99,
            AuthTier.HelloEnrolled,
            sweep: null,
            childSessions: null,
            elevationFailure: null,
            agent: null));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SessionStartFacts(
            LockAcquireOutcome.Acquired,
            OwnerRecordStatus.Absent,
            (AuthTier)99,
            sweep: null,
            childSessions: null,
            elevationFailure: null,
            agent: null));
    }

    private static SessionStartFacts Facts(
        OwnerRecordStatus recordStatus = OwnerRecordStatus.Absent,
        AuthTier authTier = AuthTier.HelloEnrolled,
        LeftoverSweepOutcome? sweep = null,
        ChildSessionsDecision? childSessions = null,
        ElevationFailure? elevationFailure = null,
        AgentAttachOutcome? agent = null)
    {
        return new SessionStartFacts(
            LockAcquireOutcome.Acquired,
            recordStatus,
            authTier,
            sweep,
            childSessions,
            elevationFailure,
            agent);
    }

    private static LeftoverSweepOutcome CompletedSweep()
    {
        return LeftoverSweepOutcome.Completed(new LeftoverSweepReport(Array.Empty<int>(), Array.Empty<int>()));
    }

    private static ChildSessionsDecision Allowed()
    {
        return ChildSessionsPolicy.Evaluate(childSessionsEnabled: true, PolicyState.Allowed, PolicyState.Allowed);
    }

    private static ChildSessionsDecision ElevationRequired()
    {
        return ChildSessionsPolicy.Evaluate(childSessionsEnabled: false, PolicyState.Allowed, PolicyState.Allowed);
    }

    private static ChildSessionsDecision Refusal(PolicyValue value, PolicyProblem problem)
    {
        PolicyState state = problem == PolicyProblem.Missing ? PolicyState.Missing : PolicyState.Denied;
        return value == PolicyValue.RdpInbound
            ? ChildSessionsPolicy.Evaluate(childSessionsEnabled: true, state, PolicyState.Allowed)
            : ChildSessionsPolicy.Evaluate(childSessionsEnabled: true, PolicyState.Allowed, state);
    }
}
