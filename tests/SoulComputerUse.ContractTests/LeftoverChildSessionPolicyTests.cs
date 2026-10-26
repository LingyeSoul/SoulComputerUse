using System;
using System.Collections.Generic;
using System.Linq;
using SoulComputerUse.Windows.Rdp;
using SoulComputerUse.Windows.Shell;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the sweep that runs before anything connects: a leftover child session must be
/// logged off first, because it would answer a new connection without asking the operator to log on.
/// </summary>
[TestClass]
public sealed class LeftoverChildSessionPolicyTests
{
    private const int ConsoleSessionId = 1;

    [TestMethod]
    public void OnlyServicesAndTheConsoleAreAlwaysSpared()
    {
        SessionCandidate services = new(0, "Services", 4);
        SessionCandidate console = new(1, "Console", 0);
        SessionCandidate child = new(15, "RDP-Tcp#0", 0);

        Assert.IsFalse(LeftoverChildSessionPolicy.IsOneOfOurs(services, ConsoleSessionId), "The service session is never ours.");
        Assert.IsFalse(LeftoverChildSessionPolicy.IsOneOfOurs(console, ConsoleSessionId), "The operator's console is never ours.");
        Assert.IsTrue(LeftoverChildSessionPolicy.IsOneOfOurs(child, ConsoleSessionId), "A station ending in #0 is the child-session shape.");
    }

    [TestMethod]
    public void ASessionWithTheConsoleIdIsSparedEvenWhenItsStationLooksLikeAChild()
    {
        SessionCandidate oddConsole = new(ConsoleSessionId, "RDP-Tcp#0", 0);

        Assert.IsFalse(
            LeftoverChildSessionPolicy.IsOneOfOurs(oddConsole, ConsoleSessionId),
            "The console session id wins over the station name; signing the operator out is far worse than a stray session.");
    }

    [TestMethod]
    public void TheRealSessionTableShapeYieldsNoLeftover()
    {
        // The measured table on this machine: service session and console, nothing else.
        List<SessionCandidate> sessions =
        [
            new(0, "Services", 4),
            new(1, "Console", 0),
        ];

        IReadOnlyList<SessionCandidate> leftovers = LeftoverChildSessionPolicy.FindLeftovers(sessions, ConsoleSessionId);

        Assert.IsEmpty(leftovers, "A machine with only the service session and the console has nothing of ours left over.");
        Assert.AreEqual(LeftoverVerdict.NoLeftover, LeftoverChildSessionPolicy.Decide(leftovers));
    }

    [TestMethod]
    public void ALeftoverChildSessionStopsTheRunUntilItIsGone()
    {
        List<SessionCandidate> sessions =
        [
            new(0, "Services", 4),
            new(1, "Console", 0),
            new(15, "RDP-Tcp#0", 0),
        ];

        IReadOnlyList<SessionCandidate> leftovers = LeftoverChildSessionPolicy.FindLeftovers(sessions, ConsoleSessionId);

        Assert.HasCount(1, leftovers, "Exactly the child session is left over.");
        Assert.AreEqual(15, leftovers[0].Id);
        Assert.AreEqual(
            LeftoverVerdict.LeftoverFound,
            LeftoverChildSessionPolicy.Decide(leftovers),
            "A logged-on child session would skip the operator's logon, so it must be handled before connecting.");
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
            new(15, "RDP-Tcp#0", 0),
            new(1, "Console", 0),
            new(0, "Services", 4),
        ];

        IReadOnlyList<SessionCandidate> leftovers = LeftoverChildSessionPolicy.FindLeftovers(sessions, ConsoleSessionId);

        Assert.HasCount(1, leftovers);
        Assert.AreEqual(15, leftovers.Single().Id);
    }
}
