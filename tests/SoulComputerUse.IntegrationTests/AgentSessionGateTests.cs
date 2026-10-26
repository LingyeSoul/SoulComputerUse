using System;
using System.Diagnostics;
using SoulComputerUse.Windows.Native;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// The agent's identity gate. The decision itself is asserted directly, and the interop that feeds
/// it is cross-checked against the session id Windows reports for the calling process, so a wrong
/// P/Invoke cannot silently make the gate pass on every machine.
/// </summary>
[TestClass]
public sealed class AgentSessionGateTests
{
    [TestMethod]
    public void AProcessInTheConsoleSessionIsRejected()
    {
        Assert.AreEqual(
            AgentSessionVerdict.ConsoleSession,
            AgentSessionGate.Evaluate(1, 1),
            "An agent that shares the console session id must be told to exit.");
    }

    [TestMethod]
    public void AProcessInAnotherSessionIsAllowed()
    {
        Assert.AreEqual(
            AgentSessionVerdict.ChildSession,
            AgentSessionGate.Evaluate(2, 1),
            "An agent in a session other than the console session may continue.");
    }

    [TestMethod]
    public void NegativeSessionIdsAreRefused()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => AgentSessionGate.Evaluate(-1, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => AgentSessionGate.Evaluate(1, -1));
    }

    [TestMethod]
    public void TheProbeAgreesWithTheSessionWindowsReportsForThisProcess()
    {
        using Process self = Process.GetCurrentProcess();
        int probeSessionId = WtsSessionProbe.GetCurrentProcessSessionId();

        Assert.AreEqual(
            self.SessionId,
            probeSessionId,
            "ProcessIdToSessionId must agree with the session id Windows reports for this process.");
        Assert.IsGreaterThanOrEqualTo(
            0,
            WtsSessionProbe.GetActiveConsoleSessionId(),
            "The active console session id must be readable on an interactive machine.");
    }
}