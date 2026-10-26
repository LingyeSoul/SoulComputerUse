using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SoulComputerUse.Core.Session;

/// <summary>
/// The error codes of section 2 of the interface contract, in the order the contract lists them.
/// The wire names are owned by <see cref="SessionErrorCodeNames"/>; retryability and the states a
/// code may occur in are owned by <see cref="SessionErrorCatalog"/>.
/// </summary>
public enum SessionErrorCode
{
    /// <summary>The single-instance lock is currently held by another process.</summary>
    LockHeld = 0,

    /// <summary>A leftover ownership record cannot be taken over safely, so a human has to deal with it.</summary>
    LockStaleRecord = 1,

    /// <summary>
    /// The lock object exists although no live session process holds it. The product cannot identify
    /// the holder with ordinary privileges and never unbinds the object on its own.
    /// </summary>
    LockForeignHandle = 2,

    /// <summary>The startup verification was declined or did not pass, so no session is established.</summary>
    AuthRequired = 3,

    /// <summary>The machine has no usable system verification path, so the operator has to configure one.</summary>
    ManualIntervention = 4,

    /// <summary>Enabling child sessions failed, either during elevation or in the system API call.</summary>
    ChildSessionsDisabled = 5,

    /// <summary>The operator declined the elevation prompt that would enable child sessions.</summary>
    ChildSessionsDeclined = 6,

    /// <summary>The machine offers no usable administrator session or elevation path.</summary>
    ElevationUnavailable = 7,

    /// <summary>Child-session connections are denied, or the policy value is missing or malformed.</summary>
    ChildConnectionsDenied = 8,

    /// <summary>Remote Desktop inbound connections are denied, or the policy value is missing or malformed.</summary>
    RdpInboundDenied = 9,

    /// <summary>The current session state does not accept the requested operation.</summary>
    State = 10,

    /// <summary>The session is still starting or connecting.</summary>
    NotReady = 11,

    /// <summary>
    /// The session waits for the operator's own system verification. The agent has no path that
    /// enters a credential on the operator's behalf.
    /// </summary>
    AwaitingCredentials = 12,

    /// <summary>The session is disconnected and needs a manual restart; there is no automatic reconnect.</summary>
    Disconnected = 13,

    /// <summary>A protected desktop or a non-interactive session blocks the operation.</summary>
    Blocked = 14,

    /// <summary>The session is shutting down.</summary>
    Closing = 15,

    /// <summary>The child-session agent missed its heartbeat.</summary>
    AgentTimeout = 16,

    /// <summary>The child-session agent process exited.</summary>
    AgentDead = 17,

    /// <summary>
    /// The agent found itself in the wrong session or failed its claim check and exited itself.
    /// This code exists only in the agent's own log and never enters the tool error channel.
    /// </summary>
    AgentWrongSession = 18,

    /// <summary>
    /// The agent was not claimed by the host within its startup window and exited itself.
    /// This code exists only in the agent's own log and never enters the tool error channel.
    /// </summary>
    AgentUnclaimed = 19,

    /// <summary>A pipe frame has a format or protocol version the peer does not accept.</summary>
    IpcProtocol = 20,

    /// <summary>The pipe peer failed its process or account check, or the handshake nonce did not match.</summary>
    IpcPeer = 21,

    /// <summary>A tool input parameter failed validation.</summary>
    InputInvalid = 22,

    /// <summary>A coordinate lies outside the child session's screen bounds.</summary>
    CoordOutOfRange = 23,

    /// <summary>The encoded screenshot exceeds the size limit.</summary>
    ScreenshotTooLarge = 24,

    /// <summary>
    /// The child-session agent failed to perform an action. Only an enumerated reason code is passed
    /// through; free-text reasons are not part of the contract.
    /// </summary>
    AgentError = 25,
}

/// <summary>
/// How an error code relates to session states. The three kinds are deliberately distinct:
/// "every state" still requires the error to name the state it occurred in, while "agent self-exit"
/// has no host session state at all because the code never leaves the agent's own log.
/// </summary>
public enum SessionErrorStateScope
{
    /// <summary>The code may occur only in the named states, and an error must name one of them.</summary>
    ListedStates = 0,

    /// <summary>The code may occur in every session state, and an error must still name it.</summary>
    EveryState = 1,

    /// <summary>
    /// The code is produced by the agent when it exits itself and never enters the tool error
    /// channel, so it carries no host session state.
    /// </summary>
    AgentSelfExit = 2,
}

/// <summary>One row of the contract's error code table: whether a code is retryable and where it may occur.</summary>
public sealed class SessionErrorRule
{
    private readonly ReadOnlyCollection<SessionState> _states;
    private readonly FrozenSet<SessionState> _allowedStates;

    /// <summary>Creates one catalog rule. Only <see cref="SessionErrorCatalog"/> calls this.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The code or one of the states is not defined.</exception>
    /// <exception cref="ArgumentException">The state list contradicts the scope or repeats a state.</exception>
    internal SessionErrorRule(
        SessionErrorCode code,
        bool retryable,
        SessionErrorStateScope stateScope,
        IReadOnlyList<SessionState> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        // Rejects an undefined enum value and keeps the code printable in every message below.
        _ = SessionErrorCodeNames.ToWireName(code);

        if (!Enum.IsDefined(stateScope))
        {
            throw new ArgumentOutOfRangeException(nameof(stateScope), stateScope, "Unknown session error state scope.");
        }

        if (stateScope == SessionErrorStateScope.ListedStates && states.Count == 0)
        {
            throw new ArgumentException(
                $"The rule for '{SessionErrorCodeNames.ToWireName(code)}' is a listed-states rule, so it needs at least one state.",
                nameof(states));
        }

        if (stateScope != SessionErrorStateScope.ListedStates && states.Count != 0)
        {
            throw new ArgumentException(
                $"The rule for '{SessionErrorCodeNames.ToWireName(code)}' must not name states outside a listed-states scope.",
                nameof(states));
        }

        SessionState[] statesInContractOrder = new SessionState[states.Count];
        HashSet<SessionState> allowedStates = new();
        for (int index = 0; index < states.Count; index++)
        {
            SessionState state = states[index];
            if (!Enum.IsDefined(state))
            {
                throw new ArgumentOutOfRangeException(nameof(states), state, "A rule lists an unknown session state.");
            }

            if (!allowedStates.Add(state))
            {
                throw new ArgumentException(
                    $"The rule for '{SessionErrorCodeNames.ToWireName(code)}' lists '{SessionStateNames.ToWireName(state)}' more than once.",
                    nameof(states));
            }

            statesInContractOrder[index] = state;
        }

        Code = code;
        Retryable = retryable;
        StateScope = stateScope;
        _states = Array.AsReadOnly(statesInContractOrder);
        _allowedStates = allowedStates.ToFrozenSet();
    }

    /// <summary>The contract error code of this rule.</summary>
    public SessionErrorCode Code { get; }

    /// <summary>Whether the contract allows retrying the operation that failed with this code.</summary>
    public bool Retryable { get; }

    /// <summary>How this code relates to session states.</summary>
    public SessionErrorStateScope StateScope { get; }

    /// <summary>
    /// The states named by a <see cref="SessionErrorStateScope.ListedStates"/> rule, in contract
    /// order. The list is empty for the other scopes, whose meaning is carried by
    /// <see cref="StateScope"/>: every state, or no host state at all.
    /// </summary>
    public IReadOnlyList<SessionState> States => _states;

    /// <summary>
    /// Tells whether this code exists only in the agent's self-exit log. Such a code is never sent
    /// through the tool error channel.
    /// </summary>
    public bool IsAgentSelfExitOnly => StateScope == SessionErrorStateScope.AgentSelfExit;

    /// <summary>Tells whether the code may occur while the session is in a given state.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The state is not a defined <see cref="SessionState"/>.</exception>
    public bool AppliesTo(SessionState state)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown session state.");
        }

        return StateScope switch
        {
            SessionErrorStateScope.ListedStates => _allowedStates.Contains(state),
            SessionErrorStateScope.EveryState => true,
            SessionErrorStateScope.AgentSelfExit => false,
            _ => throw new InvalidOperationException($"Unknown session error state scope '{StateScope}'."),
        };
    }

    /// <summary>
    /// Tells whether an error may carry the given state. A null state is accepted only by agent
    /// self-exit codes; an every-state code still has to name the state it occurred in.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The state is not a defined <see cref="SessionState"/>.</exception>
    public bool AllowsState(SessionState? state)
    {
        return state is null
            ? StateScope == SessionErrorStateScope.AgentSelfExit
            : AppliesTo(state.Value);
    }

    /// <summary>Describes the allowed states in contract wording for an error message.</summary>
    public string DescribeAllowedStates()
    {
        return StateScope switch
        {
            SessionErrorStateScope.ListedStates => string.Join(", ", _states.Select(SessionStateNames.ToWireName)),
            SessionErrorStateScope.EveryState => "every session state",
            SessionErrorStateScope.AgentSelfExit => "no host session state (agent self-exit only)",
            _ => throw new InvalidOperationException($"Unknown session error state scope '{StateScope}'."),
        };
    }
}

/// <summary>
/// The catalog of section 2 of the interface contract: one rule per error code, in table order.
/// Retryability and the states a code may occur in live here, so no caller restates them.
/// </summary>
public static class SessionErrorCatalog
{
    private static readonly IReadOnlyList<SessionErrorRule> _rules = CreateRules();
    private static readonly FrozenDictionary<SessionErrorCode, SessionErrorRule> _rulesByCode = CreateRuleIndex(_rules);

    /// <summary>Every rule in contract table order.</summary>
    public static IReadOnlyList<SessionErrorRule> Rules => _rules;

    /// <summary>Returns the rule of one code.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The code is not a defined <see cref="SessionErrorCode"/>.</exception>
    public static SessionErrorRule For(SessionErrorCode code)
    {
        if (!_rulesByCode.TryGetValue(code, out SessionErrorRule? rule))
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown session error code.");
        }

        return rule;
    }

    /// <summary>Tells whether the contract allows retrying the operation that failed with a code.</summary>
    public static bool IsRetryable(SessionErrorCode code)
    {
        return For(code).Retryable;
    }

    /// <summary>
    /// Tells whether a code exists only in the agent's self-exit log and never enters the tool error
    /// channel.
    /// </summary>
    public static bool IsAgentSelfExitOnly(SessionErrorCode code)
    {
        return For(code).IsAgentSelfExitOnly;
    }

    private static ReadOnlyCollection<SessionErrorRule> CreateRules()
    {
        SessionErrorRule[] rules =
        [
            new SessionErrorRule(
                SessionErrorCode.LockHeld,
                false,
                SessionErrorStateScope.ListedStates,
                [SessionState.Starting]),
            new SessionErrorRule(
                SessionErrorCode.LockStaleRecord,
                false,
                SessionErrorStateScope.ListedStates,
                [SessionState.Starting]),
            new SessionErrorRule(
                SessionErrorCode.LockForeignHandle,
                false,
                SessionErrorStateScope.ListedStates,
                [SessionState.Starting]),
            new SessionErrorRule(
                SessionErrorCode.AuthRequired,
                false,
                SessionErrorStateScope.ListedStates,
                [SessionState.Starting]),
            new SessionErrorRule(
                SessionErrorCode.ManualIntervention,
                false,
                SessionErrorStateScope.ListedStates,
                [SessionState.Starting]),
            new SessionErrorRule(
                SessionErrorCode.ChildSessionsDisabled,
                false,
                SessionErrorStateScope.ListedStates,
                [SessionState.Starting]),
            new SessionErrorRule(
                SessionErrorCode.ChildSessionsDeclined,
                false,
                SessionErrorStateScope.ListedStates,
                [SessionState.Starting]),
            new SessionErrorRule(
                SessionErrorCode.ElevationUnavailable,
                false,
                SessionErrorStateScope.ListedStates,
                [SessionState.Starting]),
            new SessionErrorRule(
                SessionErrorCode.ChildConnectionsDenied,
                false,
                SessionErrorStateScope.ListedStates,
                [SessionState.Starting]),
            new SessionErrorRule(
                SessionErrorCode.RdpInboundDenied,
                false,
                SessionErrorStateScope.ListedStates,
                [SessionState.Starting]),
            new SessionErrorRule(
                SessionErrorCode.State,
                false,
                SessionErrorStateScope.EveryState,
                []),
            new SessionErrorRule(
                SessionErrorCode.NotReady,
                true,
                SessionErrorStateScope.ListedStates,
                [SessionState.Starting, SessionState.Connecting]),
            new SessionErrorRule(
                SessionErrorCode.AwaitingCredentials,
                false,
                SessionErrorStateScope.ListedStates,
                [SessionState.AwaitingCredentials]),
            new SessionErrorRule(
                SessionErrorCode.Disconnected,
                false,
                SessionErrorStateScope.ListedStates,
                [SessionState.Disconnected]),
            new SessionErrorRule(
                SessionErrorCode.Blocked,
                false,
                SessionErrorStateScope.ListedStates,
                [SessionState.Blocked]),
            new SessionErrorRule(
                SessionErrorCode.Closing,
                false,
                SessionErrorStateScope.ListedStates,
                [SessionState.Closing]),
            new SessionErrorRule(
                SessionErrorCode.AgentTimeout,
                true,
                SessionErrorStateScope.ListedStates,
                [
                    SessionState.Starting,
                    SessionState.AwaitingCredentials,
                    SessionState.Connecting,
                    SessionState.Ready,
                    SessionState.Blocked,
                ]),
            new SessionErrorRule(
                SessionErrorCode.AgentDead,
                false,
                SessionErrorStateScope.ListedStates,
                [
                    SessionState.Starting,
                    SessionState.AwaitingCredentials,
                    SessionState.Connecting,
                    SessionState.Ready,
                    SessionState.Blocked,
                ]),
            new SessionErrorRule(
                SessionErrorCode.AgentWrongSession,
                false,
                SessionErrorStateScope.AgentSelfExit,
                []),
            new SessionErrorRule(
                SessionErrorCode.AgentUnclaimed,
                false,
                SessionErrorStateScope.AgentSelfExit,
                []),
            new SessionErrorRule(
                SessionErrorCode.IpcProtocol,
                false,
                SessionErrorStateScope.EveryState,
                []),
            new SessionErrorRule(
                SessionErrorCode.IpcPeer,
                false,
                SessionErrorStateScope.EveryState,
                []),
            new SessionErrorRule(
                SessionErrorCode.InputInvalid,
                false,
                SessionErrorStateScope.EveryState,
                []),
            new SessionErrorRule(
                SessionErrorCode.CoordOutOfRange,
                false,
                SessionErrorStateScope.ListedStates,
                [SessionState.Ready, SessionState.Blocked]),
            new SessionErrorRule(
                SessionErrorCode.ScreenshotTooLarge,
                false,
                SessionErrorStateScope.ListedStates,
                [SessionState.Ready, SessionState.Blocked]),
            new SessionErrorRule(
                SessionErrorCode.AgentError,
                true,
                SessionErrorStateScope.ListedStates,
                [SessionState.Ready]),
        ];

        return Array.AsReadOnly(rules);
    }

    private static FrozenDictionary<SessionErrorCode, SessionErrorRule> CreateRuleIndex(
        IReadOnlyList<SessionErrorRule> rules)
    {
        Dictionary<SessionErrorCode, SessionErrorRule> rulesByCode = new();
        foreach (SessionErrorRule rule in rules)
        {
            if (!rulesByCode.TryAdd(rule.Code, rule))
            {
                throw new InvalidOperationException(
                    $"The session error catalog declares '{SessionErrorCodeNames.ToWireName(rule.Code)}' more than once.");
            }
        }

        if (rulesByCode.Count != Enum.GetValues<SessionErrorCode>().Length)
        {
            throw new InvalidOperationException(
                "The session error catalog must declare every session error code exactly once.");
        }

        return rulesByCode.ToFrozenDictionary();
    }
}
