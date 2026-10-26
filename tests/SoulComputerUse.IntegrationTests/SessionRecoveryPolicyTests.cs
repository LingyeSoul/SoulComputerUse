using System;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// The abandoned-session recovery rules. They are applied on every successful acquisition, because
/// a dead owner leaves no abandonment event behind: the lock object simply stops existing. The rules
/// are checked exhaustively, and the combination that must never clean up is pinned explicitly.
/// </summary>
[TestClass]
public sealed class SessionRecoveryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 15, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void NoRecordMeansANormalStart()
    {
        RecoveryEvidence evidence = new(
            RecordExists: false,
            OwnerProcessAlive: false,
            OwnerProcessStartTimeMatches: false,
            RecordedState: SessionState.Ready,
            AcquiredAt: Now,
            Now: Now);

        Assert.AreEqual(RecoveryDecision.NormalStart, SessionRecoveryPolicy.Evaluate(evidence));
    }

    [TestMethod]
    public void DeadOwnerWithTerminalStateIsRecovered()
    {
        RecoveryEvidence evidence = CreateEvidence(
            ownerProcessAlive: false,
            ownerProcessStartTimeMatches: false,
            SessionState.Disconnected,
            acquiredAt: Now.AddMinutes(-5));

        Assert.AreEqual(RecoveryDecision.RecoverLeftovers, SessionRecoveryPolicy.Evaluate(evidence));
    }

    [TestMethod]
    public void DeadOwnerWithAStaleRecordIsRecoveredEvenFromABusyState()
    {
        RecoveryEvidence evidence = CreateEvidence(
            ownerProcessAlive: false,
            ownerProcessStartTimeMatches: false,
            SessionState.Ready,
            acquiredAt: Now.AddHours(-25));

        Assert.AreEqual(RecoveryDecision.RecoverLeftovers, SessionRecoveryPolicy.Evaluate(evidence));
    }

    [TestMethod]
    public void ReusedPidIsTreatedAsADeadOwner()
    {
        RecoveryEvidence evidence = CreateEvidence(
            ownerProcessAlive: true,
            ownerProcessStartTimeMatches: false,
            SessionState.Closing,
            acquiredAt: Now.AddMinutes(-5));

        Assert.AreEqual(
            RecoveryDecision.RecoverLeftovers,
            SessionRecoveryPolicy.Evaluate(evidence),
            "A live PID whose start time differs belongs to a different process, so the recorded owner is gone.");
    }

    [TestMethod]
    public void LiveOwnerWithMatchingStartTimeIsNeverCleanedUp()
    {
        RecoveryEvidence evidence = CreateEvidence(
            ownerProcessAlive: true,
            ownerProcessStartTimeMatches: true,
            SessionState.Disconnected,
            acquiredAt: Now.AddDays(-3));

        Assert.AreEqual(
            RecoveryDecision.ManualInterventionRequired,
            SessionRecoveryPolicy.Evaluate(evidence),
            "A record whose owner is still running must never be cleaned up, however old it is.");
    }

    [TestMethod]
    public void DeadOwnerWithAFreshBusyStateRequiresAHuman()
    {
        RecoveryEvidence evidence = CreateEvidence(
            ownerProcessAlive: false,
            ownerProcessStartTimeMatches: false,
            SessionState.Ready,
            acquiredAt: Now.AddMinutes(-5));

        Assert.AreEqual(
            RecoveryDecision.ManualInterventionRequired,
            SessionRecoveryPolicy.Evaluate(evidence),
            "A dead owner with a fresh non-terminal state is not enough evidence to destroy anything.");
    }

    [TestMethod]
    public void RecordIsStaleExactlyAfterTwentyFourHours()
    {
        RecoveryEvidence justUnder = CreateEvidence(
            ownerProcessAlive: false,
            ownerProcessStartTimeMatches: false,
            SessionState.Ready,
            acquiredAt: Now.AddHours(-24).AddMinutes(1));
        RecoveryEvidence justOver = CreateEvidence(
            ownerProcessAlive: false,
            ownerProcessStartTimeMatches: false,
            SessionState.Ready,
            acquiredAt: Now.AddHours(-24).AddMinutes(-1));

        Assert.AreEqual(RecoveryDecision.ManualInterventionRequired, SessionRecoveryPolicy.Evaluate(justUnder));
        Assert.AreEqual(RecoveryDecision.RecoverLeftovers, SessionRecoveryPolicy.Evaluate(justOver));
    }

    private static RecoveryEvidence CreateEvidence(
        bool ownerProcessAlive,
        bool ownerProcessStartTimeMatches,
        SessionState recordedState,
        DateTimeOffset acquiredAt)
    {
        return new RecoveryEvidence(
            RecordExists: true,
            OwnerProcessAlive: ownerProcessAlive,
            OwnerProcessStartTimeMatches: ownerProcessStartTimeMatches,
            RecordedState: recordedState,
            AcquiredAt: acquiredAt,
            Now: Now);
    }
}