using System;
using System.Collections.Generic;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting.Startup;
using SoulComputerUse.Windows.Hosting.Startup.Production;
using SoulComputerUse.Windows.Hosting.Startup.Split;
using SoulComputerUse.Windows.Ipc.Agent;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the agent attach: a pipe that does not exist yet is retried as a disconnection,
/// and every wait — the retry and the wait for the pipe call itself — goes through the injected clock, so
/// the thread that owns the product window keeps dispatching messages while the agent is still starting.
/// </summary>
[TestClass]
public sealed class ProductionAgentConnectorTests
{
    /// <summary>
    /// The timings of one fast attempt: every timeout is far below the sliced wait's ceiling, so a
    /// missing pipe fails one attempt in milliseconds instead of seconds.
    /// </summary>
    private static AgentClientTimings FastTimings { get; } = new(
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(20));

    [TestMethod]
    public void AMissingPipeIsReportedAsDisconnected()
    {
        ProductionAgentConnector connector = new(
            Guid.NewGuid().ToString("D"),
            "nonce",
            FastTimings,
            new RecordingWaitClock(),
            TimeSpan.FromMilliseconds(1));

        AgentAttachOutcome outcome = connector.Attach();

        Assert.IsFalse(outcome.Connected, "No pipe of that name exists, so the attach cannot succeed.");
        Assert.AreEqual(
            SessionErrorCode.Disconnected,
            outcome.FailureCode,
            "A missing pipe is a disconnection, which is the one code the attach window retries.");
    }

    [TestMethod]
    public void WaitingForThePipeCallGoesThroughTheInjectedClock()
    {
        RecordingWaitClock clock = new();
        ProductionAgentConnector connector = new(
            Guid.NewGuid().ToString("D"),
            "nonce",
            FastTimings,
            clock,
            TimeSpan.FromMilliseconds(1));

        _ = connector.Attach();

        Assert.IsGreaterThanOrEqualTo(
            1,
            clock.Waits,
            "The pipe call is awaited in slices on the injected clock, so a windowed caller keeps its message loop running; a plain blocking wait would freeze the product window for the whole connect timeout.");
    }

    [TestMethod]
    public void TheAttachWindowAndTheWaitClockAreValidated()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new ProductionAgentConnector("session", "nonce", FastTimings, new RecordingWaitClock(), TimeSpan.Zero));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new ProductionAgentConnector("session", "nonce", FastTimings, null!, TimeSpan.FromSeconds(1)));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new ProductionAgentConnector("session", "nonce", null!, new RecordingWaitClock(), TimeSpan.FromSeconds(1)));
        Assert.ThrowsExactly<ArgumentException>(
            () => new ProductionAgentConnector("", "nonce", FastTimings, new RecordingWaitClock(), TimeSpan.FromSeconds(1)));
        Assert.ThrowsExactly<ArgumentException>(
            () => new ProductionAgentConnector("session", "", FastTimings, new RecordingWaitClock(), TimeSpan.FromSeconds(1)));
    }

    /// <summary>A wait clock that returns at once and records every wait.</summary>
    private sealed class RecordingWaitClock : IWaitClock
    {
        private readonly List<string> _calls = [];

        internal int Waits => _calls.Count;

        public void Wait(TimeSpan duration)
        {
            Assert.IsGreaterThan(TimeSpan.Zero, duration, "The connector must only ever ask for a positive interval.");
            _calls.Add("wait");
        }
    }
}
