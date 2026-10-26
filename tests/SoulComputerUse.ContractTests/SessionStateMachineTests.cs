using System;
using System.Collections.Generic;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the session state machine: the legal transitions are transcribed from the
/// interface contract, and the whole seven-by-seven matrix is checked against them, so a missing
/// or invented transition fails loudly instead of being noticed at runtime.
/// </summary>
[TestClass]
public sealed class SessionStateMachineTests
{
    private static readonly HashSet<(SessionState From, SessionState To)> LegalTransitions = new()
    {
        (SessionState.Starting, SessionState.AwaitingCredentials),
        (SessionState.Starting, SessionState.Connecting),
        (SessionState.Starting, SessionState.Disconnected),
        (SessionState.Starting, SessionState.Closing),

        (SessionState.AwaitingCredentials, SessionState.Connecting),
        (SessionState.AwaitingCredentials, SessionState.Disconnected),
        (SessionState.AwaitingCredentials, SessionState.Closing),

        (SessionState.Connecting, SessionState.Ready),
        (SessionState.Connecting, SessionState.Disconnected),
        (SessionState.Connecting, SessionState.Blocked),
        (SessionState.Connecting, SessionState.Closing),

        (SessionState.Ready, SessionState.Disconnected),
        (SessionState.Ready, SessionState.Blocked),
        (SessionState.Ready, SessionState.Closing),

        (SessionState.Disconnected, SessionState.Closing),

        (SessionState.Blocked, SessionState.Ready),
        (SessionState.Blocked, SessionState.Disconnected),
        (SessionState.Blocked, SessionState.Closing),
    };

    [TestMethod]
    public void EveryStatePairMatchesTheContractTransitionTable()
    {
        foreach (SessionState from in Enum.GetValues<SessionState>())
        {
            foreach (SessionState to in Enum.GetValues<SessionState>())
            {
                bool expected = LegalTransitions.Contains((from, to));
                Assert.AreEqual(
                    expected,
                    SessionStateMachine.CanTransition(from, to),
                    $"The contract {(expected ? "allows" : "refuses")} {from} -> {to}.");
            }
        }
    }

    [TestMethod]
    public void NoStateMayTransitionToItself()
    {
        foreach (SessionState state in Enum.GetValues<SessionState>())
        {
            Assert.IsFalse(
                SessionStateMachine.CanTransition(state, state),
                $"{state} must not transition to itself.");
        }
    }

    [TestMethod]
    public void WireNamesRoundTripForEveryState()
    {
        Dictionary<SessionState, string> expected = new()
        {
            [SessionState.Starting] = "starting",
            [SessionState.AwaitingCredentials] = "awaiting-credentials",
            [SessionState.Connecting] = "connecting",
            [SessionState.Ready] = "ready",
            [SessionState.Disconnected] = "disconnected",
            [SessionState.Blocked] = "blocked",
            [SessionState.Closing] = "closing",
        };

        Assert.HasCount(
            Enum.GetValues<SessionState>().Length,
            expected,
            "Every state needs exactly one wire name.");

        foreach ((SessionState state, string wireName) in expected)
        {
            Assert.AreEqual(wireName, SessionStateNames.ToWireName(state));
            Assert.AreEqual(state, SessionStateNames.FromWireName(wireName));
        }
    }

    [TestMethod]
    public void UnknownWireNameIsRefused()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SessionStateNames.FromWireName("unknown-state"));
    }
}