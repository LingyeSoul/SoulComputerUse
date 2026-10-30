using System;
using System.Collections.Generic;
using System.Linq;
using SoulComputerUse.Windows.Rdp;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the sweep that runs before anything connects: a leftover child session must be
/// logged off first, because it would answer a new connection without asking the operator to log on.
/// A session that is neither provably this product's nor provably somebody else's must stop the run
/// instead, because signing out a person's disconnected desktop cannot be undone.
/// </summary>
[TestClass]
public sealed class LeftoverChildSessionPolicyTests
{
    private const int ConsoleSessionId = 1;

    private const int ActiveState = 0;

    private const string OperatorAccount = "operator";

    private static LeftoverEvidence Evidence => new(0, OperatorAccount);

    [TestMethod]
    public void OnlyServicesAndTheConsoleAreAlwaysSpared()
    {
        SessionCandidate services = new(0, "Services", LeftoverChildSessionPolicy.DisconnectedState, "SYSTEM");
        SessionCandidate console = new(1, "Console", ActiveState, OperatorAccount);

        Assert.IsFalse(LeftoverChildSessionPolicy.IsOneOfOurs(services, ConsoleSessionId, Evidence), "The service session is never ours.");
        Assert.IsFalse(LeftoverChildSessionPolicy.IsOneOfOurs(console, ConsoleSessionId, Evidence), "The operator's console is never ours.");
    }

    [TestMethod]
    public void ASessionWithTheConsoleIdIsSparedEvenWhenItsShapeLooksLikeALeftover()
    {
        SessionCandidate oddConsole = new(ConsoleSessionId, string.Empty, LeftoverChildSessionPolicy.DisconnectedState, OperatorAccount);

        Assert.IsFalse(
            LeftoverChildSessionPolicy.IsOneOfOurs(oddConsole, ConsoleSessionId, Evidence),
            "The console session id wins over every other proof; signing the operator out is far worse than a stray session.");
    }

    [TestMethod]
    public void TheRealSessionTableShapeYieldsNoLeftover()
    {
        // The measured table on this machine: service session and console, nothing else.
        List<SessionCandidate> sessions =
        [
            new(0, "Services", 4, null),
            new(1, "Console", ActiveState, OperatorAccount),
        ];

        IReadOnlyList<SessionCandidate> leftovers = LeftoverChildSessionPolicy.FindLeftovers(sessions, ConsoleSessionId, Evidence);

        Assert.IsEmpty(leftovers, "A machine with only the service session and the console has nothing of ours left over.");
        Assert.IsEmpty(LeftoverChildSessionPolicy.FindUnproven(sessions, ConsoleSessionId, Evidence));
        Assert.AreEqual(LeftoverVerdict.NoLeftover, LeftoverChildSessionPolicy.Decide(leftovers));
    }

    [TestMethod]
    public void ADetachedChildSessionOfThisAccountIsALeftover()
    {
        // Measured on the development machine on 2026-10-26: a child session whose client is gone
        // reports an empty WinStationName, the disconnected state and the account that started it.
        List<SessionCandidate> sessions =
        [
            new(0, "Services", 4, null),
            new(1, "Console", ActiveState, OperatorAccount),
            new(15, string.Empty, LeftoverChildSessionPolicy.DisconnectedState, OperatorAccount),
        ];

        IReadOnlyList<SessionCandidate> leftovers = LeftoverChildSessionPolicy.FindLeftovers(sessions, ConsoleSessionId, Evidence);

        Assert.HasCount(1, leftovers, "Exactly the detached child session of this account is left over.");
        Assert.AreEqual(15, leftovers[0].Id);
        Assert.AreEqual(
            LeftoverVerdict.LeftoverFound,
            LeftoverChildSessionPolicy.Decide(leftovers),
            "A logged-on child session would skip the operator's logon, so it must be handled before connecting.");
    }

    [TestMethod]
    public void ADetachedSessionOfAnotherAccountIsNeverSwept()
    {
        // A fast user switch leaves the other local user's session behind in exactly this shape. The
        // account name is the only fact that separates it from this product's leftover.
        SessionCandidate otherUser = new(2, string.Empty, LeftoverChildSessionPolicy.DisconnectedState, "somebody-else");

        Assert.IsFalse(
            LeftoverChildSessionPolicy.IsOneOfOurs(otherUser, ConsoleSessionId, Evidence),
            "A session of another account is not this product's leftover, whatever its station name says.");

        Assert.IsEmpty(LeftoverChildSessionPolicy.FindLeftovers([otherUser], ConsoleSessionId, Evidence));
        Assert.IsEmpty(LeftoverChildSessionPolicy.FindUnproven([otherUser], ConsoleSessionId, Evidence));
    }

    [TestMethod]
    public void AnActiveInboundRdpSessionWithAReadableAccountIsNeverSwept()
    {
        // The product's own prerequisite keeps inbound RDP enabled, so a person working on this machine
        // over RDP shows up as an active RDP-Tcp#n station. A leftover's client is gone by definition,
        // so an active session is never this product's leftover.
        SessionCandidate workingPerson = new(7, "RDP-Tcp#0", ActiveState, OperatorAccount);

        Assert.IsFalse(
            LeftoverChildSessionPolicy.IsOneOfOurs(workingPerson, ConsoleSessionId, Evidence),
            "An active session has somebody in front of it; the sweep may never sign that person out.");

        Assert.IsEmpty(LeftoverChildSessionPolicy.FindLeftovers([workingPerson], ConsoleSessionId, Evidence));
        Assert.IsEmpty(LeftoverChildSessionPolicy.FindUnproven([workingPerson], ConsoleSessionId, Evidence));
    }

    [TestMethod]
    public void StationNumbersBeyondZeroAreNotMatchedByASuffixCoincidence()
    {
        // The old rule matched any station ending in '#0', so RDP-Tcp#10, RDP-Tcp#20 and every count
        // ending in zero were treated as this product's session. The whole station must be the inbound
        // listener's shape; a suffix is not a shape.
        SessionCandidate ten = new(3, "RDP-Tcp#10", LeftoverChildSessionPolicy.DisconnectedState, null);
        SessionCandidate twenty = new(4, "RDP-Tcp#20", ActiveState, null);
        SessionCandidate notARdpStation = new(5, "services-#0", LeftoverChildSessionPolicy.DisconnectedState, null);

        Assert.IsFalse(LeftoverChildSessionPolicy.IsOneOfOurs(ten, ConsoleSessionId, Evidence), "A suffix match must never make a session ours.");
        Assert.IsFalse(LeftoverChildSessionPolicy.IsOneOfOurs(twenty, ConsoleSessionId, Evidence), "A suffix match must never make a session ours.");
        Assert.IsFalse(LeftoverChildSessionPolicy.IsOneOfOurs(notARdpStation, ConsoleSessionId, Evidence), "A station that merely ends in '#0' is not an inbound remote-desktop station at all.");
    }

    [TestMethod]
    public void ADetachedInboundRdpSessionWithAReadableAccountStopsTheRunForAHuman()
    {
        // A person who closes the remote-desktop window while staying logged on leaves exactly this
        // shape, and so may an unmeasured leftover of this product. No test can tell them apart either,
        // so the policy must not: neither a logoff nor a connection may guess.
        SessionCandidate disconnectedPerson = new(9, "RDP-Tcp#3", LeftoverChildSessionPolicy.DisconnectedState, OperatorAccount);

        Assert.AreEqual(
            LeftoverProof.Unproven,
            LeftoverChildSessionPolicy.Classify(disconnectedPerson, ConsoleSessionId, Evidence),
            "A detached inbound remote-desktop session with a readable account is neither provably ours nor provably foreign.");

        IReadOnlyList<SessionCandidate> unproven = LeftoverChildSessionPolicy.FindUnproven([disconnectedPerson], ConsoleSessionId, Evidence);
        Assert.HasCount(1, unproven);
        Assert.IsEmpty(LeftoverChildSessionPolicy.FindLeftovers([disconnectedPerson], ConsoleSessionId, Evidence), "An unprovable session must never be logged off.");
    }

    [TestMethod]
    public void TheRecordedChildSessionIdProvesTheLeftoverWhenTheAccountAgrees()
    {
        LeftoverEvidence evidence = new(15, OperatorAccount);
        SessionCandidate named = new(15, string.Empty, LeftoverChildSessionPolicy.DisconnectedState, OperatorAccount);
        SessionCandidate namedWithAnyStation = new(15, "RDP-Tcp#7", LeftoverChildSessionPolicy.DisconnectedState, OperatorAccount);

        Assert.IsTrue(
            LeftoverChildSessionPolicy.IsOneOfOurs(named, ConsoleSessionId, evidence),
            "The record this product wrote names this exact session, and the account agrees, so the id is proof.");
        Assert.IsTrue(
            LeftoverChildSessionPolicy.IsOneOfOurs(namedWithAnyStation, ConsoleSessionId, evidence),
            "The recorded id is proof regardless of the station name, because the record already names the session.");

        SessionCandidate otherAccount = new(15, string.Empty, LeftoverChildSessionPolicy.DisconnectedState, "somebody-else");
        SessionCandidate otherState = new(15, string.Empty, ActiveState, OperatorAccount);

        Assert.IsFalse(
            LeftoverChildSessionPolicy.IsOneOfOurs(otherAccount, ConsoleSessionId, evidence),
            "A session of another account is not this product's leftover even when the id matches; a reboot can hand the id to somebody else.");
        Assert.IsFalse(
            LeftoverChildSessionPolicy.IsOneOfOurs(otherState, ConsoleSessionId, evidence),
            "An active session is never a leftover, not even for the recorded id, because somebody may be working in it.");
    }

    [TestMethod]
    public void ARecordedIdWhoseAccountCannotBeReadStopsTheRunForAHuman()
    {
        LeftoverEvidence evidence = new(15, OperatorAccount);
        SessionCandidate unreadable = new(15, string.Empty, LeftoverChildSessionPolicy.DisconnectedState, null);

        Assert.AreEqual(
            LeftoverProof.Unproven,
            LeftoverChildSessionPolicy.Classify(unreadable, ConsoleSessionId, evidence),
            "The record names this exact session and the account could not be checked, so neither a logoff nor a connection may guess.");
    }

    [TestMethod]
    public void AnUnreadableAccountNameNeverProvesASessionOurs()
    {
        SessionCandidate unreadable = new(2, string.Empty, LeftoverChildSessionPolicy.DisconnectedState, null);

        Assert.IsFalse(
            LeftoverChildSessionPolicy.IsOneOfOurs(unreadable, ConsoleSessionId, Evidence),
            "The system refuses to answer for another account's session, so an unreadable name is absent proof, never a match.");
        Assert.IsEmpty(LeftoverChildSessionPolicy.FindUnproven([unreadable], ConsoleSessionId, Evidence));

        LeftoverEvidence noOperatorName = new(0, null);
        Assert.IsFalse(
            LeftoverChildSessionPolicy.IsOneOfOurs(unreadable, ConsoleSessionId, noOperatorName),
            "Without the operator's own account name nothing can be proven to run as this account.");
        SessionCandidate readable = new(3, string.Empty, LeftoverChildSessionPolicy.DisconnectedState, OperatorAccount);
        Assert.IsFalse(
            LeftoverChildSessionPolicy.IsOneOfOurs(readable, ConsoleSessionId, noOperatorName),
            "Without the operator's own account name nothing can be proven to run as this account.");
    }

    [TestMethod]
    public void AnActiveSessionWithoutAStationNameIsNotALeftover()
    {
        SessionCandidate stillStarting = new(7, string.Empty, ActiveState, OperatorAccount);

        Assert.IsFalse(
            LeftoverChildSessionPolicy.IsOneOfOurs(stillStarting, ConsoleSessionId, Evidence),
            "A session Windows is still creating has no station name yet either, and signing it off would be a guess.");
    }

    [TestMethod]
    public void TheRefusalNamesManualInterventionAndTheReason()
    {
        InvalidOperationException error = LeftoverChildSessionPolicy.ManualInterventionRequired("child session 15 could not be logged off.");

        Assert.Contains("manual_intervention_required", error.Message);
        Assert.Contains("bypassed", error.Message);
        Assert.ThrowsExactly<ArgumentException>(() => LeftoverChildSessionPolicy.ManualInterventionRequired("  "));
    }

    [TestMethod]
    public void TheSweepDoesNotDependOnTheOrderOfTheTable()
    {
        List<SessionCandidate> sessions =
        [
            new(15, string.Empty, LeftoverChildSessionPolicy.DisconnectedState, OperatorAccount),
            new(1, "Console", ActiveState, OperatorAccount),
            new(0, "Services", 4, null),
        ];

        IReadOnlyList<SessionCandidate> leftovers = LeftoverChildSessionPolicy.FindLeftovers(sessions, ConsoleSessionId, Evidence);

        Assert.HasCount(1, leftovers);
        Assert.AreEqual(15, leftovers.Single().Id);
    }

    [TestMethod]
    public void TheEvidenceRefusesANegativeRecordedSessionId()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new LeftoverEvidence(-1, OperatorAccount));
    }

    [TestMethod]
    public void ATearDownTargetTheRecordNamesAndThisAccountRunsAsIsOursWhateverItsState()
    {
        // A teardown runs while the single-instance lock is held and may itself still be the target's
        // client, so the sweep's rule that an active session is never ours does not apply here: the
        // recorded id plus the same account is the whole proof, the state is not part of it.
        LeftoverEvidence evidence = new(15, OperatorAccount);

        Assert.AreEqual(
            LeftoverProof.Ours,
            LeftoverChildSessionPolicy.ClassifyOwnedTarget(
                new SessionCandidate(15, string.Empty, LeftoverChildSessionPolicy.DisconnectedState, OperatorAccount),
                ConsoleSessionId,
                evidence),
            "A disconnected target of this account that the record names is ours.");
        Assert.AreEqual(
            LeftoverProof.Ours,
            LeftoverChildSessionPolicy.ClassifyOwnedTarget(
                new SessionCandidate(15, "RDP-Tcp#7", ActiveState, OperatorAccount),
                ConsoleSessionId,
                evidence),
            "A still-connected target of this account that the record names is ours too, because the teardown may be its client.");
    }

    [TestMethod]
    public void ATearDownTargetIsRefusedUnlessTheRecordNamesItAndItRunsAsThisAccount()
    {
        SessionCandidate named = new(15, string.Empty, LeftoverChildSessionPolicy.DisconnectedState, OperatorAccount);
        SessionCandidate stranger = new(22, string.Empty, LeftoverChildSessionPolicy.DisconnectedState, "stranger");
        SessionCandidate nobodyLoggedOn = new(15, string.Empty, LeftoverChildSessionPolicy.DisconnectedState, string.Empty);
        SessionCandidate console = new(1, "Console", ActiveState, OperatorAccount);
        SessionCandidate service = new(0, "Services", 4, null);

        Assert.AreEqual(LeftoverProof.NotOurs, LeftoverChildSessionPolicy.ClassifyOwnedTarget(stranger, ConsoleSessionId, new(15, OperatorAccount)));
        Assert.AreEqual(LeftoverProof.NotOurs, LeftoverChildSessionPolicy.ClassifyOwnedTarget(nobodyLoggedOn, ConsoleSessionId, new(15, OperatorAccount)));
        Assert.AreEqual(LeftoverProof.NotOurs, LeftoverChildSessionPolicy.ClassifyOwnedTarget(console, ConsoleSessionId, new(1, OperatorAccount)));
        Assert.AreEqual(LeftoverProof.NotOurs, LeftoverChildSessionPolicy.ClassifyOwnedTarget(service, ConsoleSessionId, new(0, OperatorAccount)));

        // A session the evidence does not name needs a sweep with its own proof, never a teardown.
        Assert.AreEqual(LeftoverProof.NotOurs, LeftoverChildSessionPolicy.ClassifyOwnedTarget(named, ConsoleSessionId, Evidence));
    }

    [TestMethod]
    public void ATearDownTargetWhoseAccountCannotBeReadStaysUnproven()
    {
        // The system did not answer for the target's account, so neither ownership nor foreignness is
        // proven; a teardown must refuse rather than guess in either direction.
        Assert.AreEqual(
            LeftoverProof.Unproven,
            LeftoverChildSessionPolicy.ClassifyOwnedTarget(
                new SessionCandidate(15, string.Empty, LeftoverChildSessionPolicy.DisconnectedState, null),
                ConsoleSessionId,
                new(15, OperatorAccount)));
    }
}
