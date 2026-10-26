using System;
using System.Collections.Generic;
using System.Linq;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for section 2 of the interface contract. The expected tables below are
/// transcribed from the contract by hand, independently of the catalog under test, so a changed
/// retryability value, state set or wire name fails loudly instead of being read back from the code.
/// </summary>
[TestClass]
public sealed class SessionErrorTests
{
    /// <summary>Section 2 of the interface contract lists exactly this many error codes.</summary>
    private const int ContractCodeCount = 26;

    private static readonly string[] ExpectedWireNameOrder =
    [
        "E_LOCK_HELD",
        "E_LOCK_STALE_RECORD",
        "E_LOCK_FOREIGN_HANDLE",
        "E_AUTH_REQUIRED",
        "E_MANUAL_INTERVENTION",
        "E_CHILD_SESSIONS_DISABLED",
        "E_CHILD_SESSIONS_DECLINED",
        "E_ELEVATION_UNAVAILABLE",
        "E_CHILD_CONNECTIONS_DENIED",
        "E_RDP_INBOUND_DENIED",
        "E_STATE",
        "E_NOT_READY",
        "E_AWAITING_CREDENTIALS",
        "E_DISCONNECTED",
        "E_BLOCKED",
        "E_CLOSING",
        "E_AGENT_TIMEOUT",
        "E_AGENT_DEAD",
        "E_AGENT_WRONG_SESSION",
        "E_AGENT_UNCLAIMED",
        "E_IPC_PROTOCOL",
        "E_IPC_PEER",
        "E_INPUT_INVALID",
        "E_COORD_OUT_OF_RANGE",
        "E_SCREENSHOT_TOO_LARGE",
        "E_AGENT_ERROR",
    ];

    private static readonly Dictionary<SessionErrorCode, string> ExpectedWireNames = new()
    {
        [SessionErrorCode.LockHeld] = "E_LOCK_HELD",
        [SessionErrorCode.LockStaleRecord] = "E_LOCK_STALE_RECORD",
        [SessionErrorCode.LockForeignHandle] = "E_LOCK_FOREIGN_HANDLE",
        [SessionErrorCode.AuthRequired] = "E_AUTH_REQUIRED",
        [SessionErrorCode.ManualIntervention] = "E_MANUAL_INTERVENTION",
        [SessionErrorCode.ChildSessionsDisabled] = "E_CHILD_SESSIONS_DISABLED",
        [SessionErrorCode.ChildSessionsDeclined] = "E_CHILD_SESSIONS_DECLINED",
        [SessionErrorCode.ElevationUnavailable] = "E_ELEVATION_UNAVAILABLE",
        [SessionErrorCode.ChildConnectionsDenied] = "E_CHILD_CONNECTIONS_DENIED",
        [SessionErrorCode.RdpInboundDenied] = "E_RDP_INBOUND_DENIED",
        [SessionErrorCode.State] = "E_STATE",
        [SessionErrorCode.NotReady] = "E_NOT_READY",
        [SessionErrorCode.AwaitingCredentials] = "E_AWAITING_CREDENTIALS",
        [SessionErrorCode.Disconnected] = "E_DISCONNECTED",
        [SessionErrorCode.Blocked] = "E_BLOCKED",
        [SessionErrorCode.Closing] = "E_CLOSING",
        [SessionErrorCode.AgentTimeout] = "E_AGENT_TIMEOUT",
        [SessionErrorCode.AgentDead] = "E_AGENT_DEAD",
        [SessionErrorCode.AgentWrongSession] = "E_AGENT_WRONG_SESSION",
        [SessionErrorCode.AgentUnclaimed] = "E_AGENT_UNCLAIMED",
        [SessionErrorCode.IpcProtocol] = "E_IPC_PROTOCOL",
        [SessionErrorCode.IpcPeer] = "E_IPC_PEER",
        [SessionErrorCode.InputInvalid] = "E_INPUT_INVALID",
        [SessionErrorCode.CoordOutOfRange] = "E_COORD_OUT_OF_RANGE",
        [SessionErrorCode.ScreenshotTooLarge] = "E_SCREENSHOT_TOO_LARGE",
        [SessionErrorCode.AgentError] = "E_AGENT_ERROR",
    };

    private static readonly Dictionary<SessionErrorCode, bool> ExpectedRetryable = new()
    {
        [SessionErrorCode.LockHeld] = false,
        [SessionErrorCode.LockStaleRecord] = false,
        [SessionErrorCode.LockForeignHandle] = false,
        [SessionErrorCode.AuthRequired] = false,
        [SessionErrorCode.ManualIntervention] = false,
        [SessionErrorCode.ChildSessionsDisabled] = false,
        [SessionErrorCode.ChildSessionsDeclined] = false,
        [SessionErrorCode.ElevationUnavailable] = false,
        [SessionErrorCode.ChildConnectionsDenied] = false,
        [SessionErrorCode.RdpInboundDenied] = false,
        [SessionErrorCode.State] = false,
        [SessionErrorCode.NotReady] = true,
        [SessionErrorCode.AwaitingCredentials] = false,
        [SessionErrorCode.Disconnected] = false,
        [SessionErrorCode.Blocked] = false,
        [SessionErrorCode.Closing] = false,
        [SessionErrorCode.AgentTimeout] = true,
        [SessionErrorCode.AgentDead] = false,
        [SessionErrorCode.AgentWrongSession] = false,
        [SessionErrorCode.AgentUnclaimed] = false,
        [SessionErrorCode.IpcProtocol] = false,
        [SessionErrorCode.IpcPeer] = false,
        [SessionErrorCode.InputInvalid] = false,
        [SessionErrorCode.CoordOutOfRange] = false,
        [SessionErrorCode.ScreenshotTooLarge] = false,
        [SessionErrorCode.AgentError] = true,
    };

    private static readonly Dictionary<SessionErrorCode, SessionState[]> ExpectedListedStates = new()
    {
        [SessionErrorCode.LockHeld] = [SessionState.Starting],
        [SessionErrorCode.LockStaleRecord] = [SessionState.Starting],
        [SessionErrorCode.LockForeignHandle] = [SessionState.Starting],
        [SessionErrorCode.AuthRequired] = [SessionState.Starting],
        [SessionErrorCode.ManualIntervention] = [SessionState.Starting],
        [SessionErrorCode.ChildSessionsDisabled] = [SessionState.Starting],
        [SessionErrorCode.ChildSessionsDeclined] = [SessionState.Starting],
        [SessionErrorCode.ElevationUnavailable] = [SessionState.Starting],
        [SessionErrorCode.ChildConnectionsDenied] = [SessionState.Starting],
        [SessionErrorCode.RdpInboundDenied] = [SessionState.Starting],
        [SessionErrorCode.NotReady] = [SessionState.Starting, SessionState.Connecting],
        [SessionErrorCode.AwaitingCredentials] = [SessionState.AwaitingCredentials],
        [SessionErrorCode.Disconnected] = [SessionState.Disconnected],
        [SessionErrorCode.Blocked] = [SessionState.Blocked],
        [SessionErrorCode.Closing] = [SessionState.Closing],
        [SessionErrorCode.AgentTimeout] =
        [
            SessionState.Starting,
            SessionState.AwaitingCredentials,
            SessionState.Connecting,
            SessionState.Ready,
            SessionState.Blocked,
        ],
        [SessionErrorCode.AgentDead] =
        [
            SessionState.Starting,
            SessionState.AwaitingCredentials,
            SessionState.Connecting,
            SessionState.Ready,
            SessionState.Blocked,
        ],
        [SessionErrorCode.CoordOutOfRange] = [SessionState.Ready, SessionState.Blocked],
        [SessionErrorCode.ScreenshotTooLarge] = [SessionState.Ready, SessionState.Blocked],
        [SessionErrorCode.AgentError] = [SessionState.Ready],
    };

    private static readonly HashSet<SessionErrorCode> ExpectedEveryStateCodes =
    [
        SessionErrorCode.State,
        SessionErrorCode.IpcProtocol,
        SessionErrorCode.IpcPeer,
        SessionErrorCode.InputInvalid,
    ];

    private static readonly HashSet<SessionErrorCode> ExpectedAgentSelfExitCodes =
    [
        SessionErrorCode.AgentWrongSession,
        SessionErrorCode.AgentUnclaimed,
    ];

    [TestMethod]
    public void TheEnumAndTheCatalogHaveExactlyTheContractRowCount()
    {
        Assert.HasCount(
            ContractCodeCount,
            Enum.GetValues<SessionErrorCode>(),
            "Section 2 lists 26 codes; the enum must have one member per row and no more.");
        Assert.HasCount(ContractCodeCount, SessionErrorCatalog.Rules, "The catalog must have one rule per row.");
        Assert.HasCount(ContractCodeCount, ExpectedWireNames, "The independent wire name table must cover every row.");
        Assert.HasCount(ContractCodeCount, ExpectedRetryable, "The independent retryable table must cover every row.");
    }

    [TestMethod]
    public void TheEnumMembersFollowTheContractTableOrder()
    {
        string[] actualOrder = Enum.GetValues<SessionErrorCode>()
            .Select(SessionErrorCodeNames.ToWireName)
            .ToArray();
        Assert.AreEqual(
            string.Join(", ", ExpectedWireNameOrder),
            string.Join(", ", actualOrder),
            "The enum and the catalog must follow the order of the contract table.");

        string[] catalogOrder = SessionErrorCatalog.Rules
            .Select(rule => SessionErrorCodeNames.ToWireName(rule.Code))
            .ToArray();
        Assert.AreEqual(
            string.Join(", ", ExpectedWireNameOrder),
            string.Join(", ", catalogOrder),
            "The catalog must follow the order of the contract table.");
    }

    [TestMethod]
    public void EveryCodeHasOneUniqueWireNameAndRoundTrips()
    {
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (SessionErrorCode code in Enum.GetValues<SessionErrorCode>())
        {
            Assert.IsTrue(
                ExpectedWireNames.TryGetValue(code, out string? expected),
                $"The contract has no wire name for '{code}', so the enum invented a code.");

            string actual = SessionErrorCodeNames.ToWireName(code);
            Assert.AreEqual(expected, actual, $"'{code}' has the wrong wire name.");
            Assert.IsTrue(seen.Add(actual), $"The wire name '{actual}' is used by more than one code.");
            Assert.AreEqual(code, SessionErrorCodeNames.FromWireName(actual), $"'{actual}' must round-trip.");
        }

        Assert.HasCount(ContractCodeCount, seen, "Every contract code needs exactly one distinct wire name.");
    }

    [TestMethod]
    public void AnUnknownWireNameIsRefusedAndNamed()
    {
        const string Unknown = "E_NOT_IN_THE_CONTRACT";

        ArgumentOutOfRangeException error = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SessionErrorCodeNames.FromWireName(Unknown));

        Assert.Contains(Unknown, error.Message);
    }

    [TestMethod]
    public void AnUnknownEnumValueIsRefusedAndNamed()
    {
        SessionErrorCode unknownWire = (SessionErrorCode)999;
        ArgumentOutOfRangeException wireError = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SessionErrorCodeNames.ToWireName(unknownWire));
        Assert.Contains("999", wireError.Message);

        SessionErrorCode unknownCatalog = (SessionErrorCode)(-1);
        ArgumentOutOfRangeException catalogError = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SessionErrorCatalog.For(unknownCatalog));
        Assert.Contains("-1", catalogError.Message);
    }

    [TestMethod]
    public void EveryCodesRetryabilityMatchesTheContractTable()
    {
        foreach (SessionErrorCode code in Enum.GetValues<SessionErrorCode>())
        {
            Assert.IsTrue(
                ExpectedRetryable.TryGetValue(code, out bool expected),
                $"The contract table has no retryable value for '{code}'.");
            Assert.AreEqual(
                expected,
                SessionErrorCatalog.For(code).Retryable,
                $"'{SessionErrorCodeNames.ToWireName(code)}' has the wrong retryable value.");
        }
    }

    [TestMethod]
    public void EveryCodeOccursOnlyInTheStatesTheContractAllows()
    {
        int coveredRows = ExpectedListedStates.Count + ExpectedEveryStateCodes.Count + ExpectedAgentSelfExitCodes.Count;
        Assert.AreEqual(
            ContractCodeCount,
            coveredRows,
            "The independent state tables must cover every contract row exactly once.");

        foreach (SessionErrorCode code in Enum.GetValues<SessionErrorCode>())
        {
            SessionErrorRule rule = SessionErrorCatalog.For(code);

            if (ExpectedAgentSelfExitCodes.Contains(code))
            {
                Assert.AreEqual(SessionErrorStateScope.AgentSelfExit, rule.StateScope, $"'{code}' is agent self-exit only.");
                Assert.IsEmpty(rule.States, "Agent self-exit codes name no host state.");
                Assert.IsTrue(rule.IsAgentSelfExitOnly);
                Assert.IsTrue(rule.AllowsState(null), "Agent self-exit codes carry no host state.");
                foreach (SessionState state in Enum.GetValues<SessionState>())
                {
                    Assert.IsFalse(rule.AllowsState(state), $"'{code}' must not occur in {state}.");
                }

                continue;
            }

            Assert.IsFalse(rule.IsAgentSelfExitOnly, $"'{code}' belongs to the tool error channel.");

            if (ExpectedEveryStateCodes.Contains(code))
            {
                Assert.AreEqual(
                    SessionErrorStateScope.EveryState,
                    rule.StateScope,
                    $"'{code}' is allowed in every session state.");
                Assert.IsEmpty(rule.States, "An every-state code does not list states; its scope says every state.");
                foreach (SessionState state in Enum.GetValues<SessionState>())
                {
                    Assert.IsTrue(rule.AllowsState(state), $"'{code}' must occur in {state}.");
                }

                Assert.IsFalse(
                    rule.AllowsState(null),
                    "An every-state code still has to name the state it occurred in; null is not a wildcard.");
                continue;
            }

            Assert.IsTrue(
                ExpectedListedStates.TryGetValue(code, out SessionState[]? expectedStates),
                $"The contract state table has no entry for '{code}'.");
            Assert.AreEqual(SessionErrorStateScope.ListedStates, rule.StateScope, $"'{code}' must list its states.");
            Assert.AreEqual(
                string.Join(", ", expectedStates.Select(SessionStateNames.ToWireName)),
                string.Join(", ", rule.States.Select(SessionStateNames.ToWireName)),
                $"'{code}' lists the wrong states.");
            Assert.IsFalse(rule.AllowsState(null), $"'{code}' must name one of its states.");

            foreach (SessionState state in Enum.GetValues<SessionState>())
            {
                Assert.AreEqual(
                    expectedStates.Contains(state),
                    rule.AllowsState(state),
                    $"'{SessionErrorCodeNames.ToWireName(code)}' decides {state} wrongly.");
            }
        }
    }

    [TestMethod]
    public void TheCatalogExposesTheSameRetryabilityAndAgentFlag()
    {
        foreach (SessionErrorCode code in Enum.GetValues<SessionErrorCode>())
        {
            SessionErrorRule rule = SessionErrorCatalog.For(code);
            Assert.AreEqual(rule.Retryable, SessionErrorCatalog.IsRetryable(code));
            Assert.AreEqual(rule.IsAgentSelfExitOnly, SessionErrorCatalog.IsAgentSelfExitOnly(code));
        }
    }

    [TestMethod]
    public void AgentSelfExitCodesStayOutOfTheToolErrorChannel()
    {
        Assert.IsTrue(SessionErrorCatalog.For(SessionErrorCode.AgentWrongSession).IsAgentSelfExitOnly);
        Assert.IsTrue(SessionErrorCatalog.For(SessionErrorCode.AgentUnclaimed).IsAgentSelfExitOnly);
        Assert.IsFalse(
            SessionErrorCatalog.For(SessionErrorCode.AgentDead).IsAgentSelfExitOnly,
            "A dead agent is reported to tools; only the two self-exit codes are log-only.");

        int agentSelfExitCount = Enum.GetValues<SessionErrorCode>()
            .Count(SessionErrorCatalog.IsAgentSelfExitOnly);
        Assert.AreEqual(2, agentSelfExitCount, "Only the two agent self-exit codes stay out of the tool channel.");
    }

    [TestMethod]
    public void SessionErrorKeepsTheFourContractFields()
    {
        SessionError error = new SessionError(
            SessionErrorCode.AgentError,
            "window-gone",
            true,
            SessionState.Ready);

        Assert.AreEqual(SessionErrorCode.AgentError, error.Code);
        Assert.AreEqual("window-gone", error.Message);
        Assert.IsTrue(error.Retryable);
        Assert.AreEqual(SessionState.Ready, error.State);
        Assert.AreEqual(
            error,
            new SessionError(SessionErrorCode.AgentError, "window-gone", true, SessionState.Ready),
            "Two error objects with the same four fields are the same value.");
    }

    [TestMethod]
    public void SessionErrorRefusesAnEmptyMessage()
    {
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(
            () => new SessionError(SessionErrorCode.LockHeld, string.Empty, false, SessionState.Starting));

        Assert.AreEqual("message", error.ParamName);
    }

    [TestMethod]
    public void SessionErrorRefusesAWhitespaceOnlyMessage()
    {
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(
            () => new SessionError(SessionErrorCode.LockHeld, "   ", false, SessionState.Starting));

        Assert.AreEqual("message", error.ParamName);
    }

    [TestMethod]
    public void SessionErrorRefusesARetryableValueThatContradictsTheCatalog()
    {
        ArgumentException claimedRetryable = Assert.ThrowsExactly<ArgumentException>(
            () => new SessionError(SessionErrorCode.LockHeld, "lock held", true, SessionState.Starting));
        Assert.AreEqual("retryable", claimedRetryable.ParamName);
        Assert.Contains("E_LOCK_HELD", claimedRetryable.Message);

        ArgumentException claimedTerminal = Assert.ThrowsExactly<ArgumentException>(
            () => new SessionError(SessionErrorCode.NotReady, "not ready", false, SessionState.Starting));
        Assert.AreEqual("retryable", claimedTerminal.ParamName);
        Assert.Contains("E_NOT_READY", claimedTerminal.Message);
    }

    [TestMethod]
    public void SessionErrorRefusesAStateTheCodeCannotOccurIn()
    {
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(
            () => new SessionError(SessionErrorCode.LockHeld, "lock held", false, SessionState.Ready));

        Assert.AreEqual("state", error.ParamName);
        Assert.Contains("E_LOCK_HELD", error.Message);
        Assert.Contains("starting", error.Message);
    }

    [TestMethod]
    public void SessionErrorRefusesAMissingStateForACodeThatNeedsOne()
    {
        ArgumentException listed = Assert.ThrowsExactly<ArgumentException>(
            () => new SessionError(SessionErrorCode.LockHeld, "lock held", false, null));
        Assert.AreEqual("state", listed.ParamName);

        ArgumentException everyState = Assert.ThrowsExactly<ArgumentException>(
            () => new SessionError(SessionErrorCode.State, "wrong state", false, null));
        Assert.AreEqual("state", everyState.ParamName);
        Assert.Contains("E_STATE", everyState.Message);
    }

    [TestMethod]
    public void SessionErrorAcceptsANullStateForAgentSelfExitCodes()
    {
        SessionError error = new SessionError(
            SessionErrorCode.AgentWrongSession,
            "claim gate hit",
            false,
            null);

        Assert.IsNull(error.State);
        Assert.AreEqual(SessionErrorCode.AgentWrongSession, error.Code);

        ArgumentException withState = Assert.ThrowsExactly<ArgumentException>(
            () => new SessionError(
                SessionErrorCode.AgentUnclaimed,
                "not claimed",
                false,
                SessionState.Ready));
        Assert.AreEqual("state", withState.ParamName);
        Assert.Contains("E_AGENT_UNCLAIMED", withState.Message);
    }

    [TestMethod]
    public void TheCreateFactoryTakesRetryabilityFromTheCatalog()
    {
        SessionError retryable = SessionError.Create(
            SessionErrorCode.AgentTimeout,
            "heartbeat missing",
            SessionState.Ready);
        Assert.IsTrue(retryable.Retryable);
        Assert.AreEqual(SessionErrorCatalog.For(SessionErrorCode.AgentTimeout).Retryable, retryable.Retryable);

        SessionError terminal = SessionError.Create(
            SessionErrorCode.LockHeld,
            "lock held",
            SessionState.Starting);
        Assert.IsFalse(terminal.Retryable);
    }

    [TestMethod]
    public void ConstructionFollowsTheCatalogForEveryCodeAndState()
    {
        foreach (SessionErrorCode code in Enum.GetValues<SessionErrorCode>())
        {
            SessionErrorRule rule = SessionErrorCatalog.For(code);

            foreach (SessionState state in Enum.GetValues<SessionState>())
            {
                if (rule.AllowsState(state))
                {
                    SessionError error = new SessionError(code, "contract message", rule.Retryable, state);
                    Assert.AreEqual(code, error.Code);
                    Assert.AreEqual(state, error.State);
                    Assert.AreEqual(rule.Retryable, error.Retryable);
                }
                else
                {
                    Assert.ThrowsExactly<ArgumentException>(
                        () => new SessionError(code, "contract message", rule.Retryable, state));
                }
            }

            if (rule.AllowsState(null))
            {
                SessionError error = new SessionError(code, "contract message", rule.Retryable, null);
                Assert.IsNull(error.State);
            }
            else
            {
                Assert.ThrowsExactly<ArgumentException>(
                    () => new SessionError(code, "contract message", rule.Retryable, null));
            }
        }
    }
}
