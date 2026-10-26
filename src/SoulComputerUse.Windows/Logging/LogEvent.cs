using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace SoulComputerUse.Windows.Logging;

/// <summary>
/// The fixed set of events one structured log line may carry. The events are the ones the interface
/// contract names; a new event needs a contract revision, so no caller may invent a name.
/// </summary>
/// <remarks>
/// The wire names live in <see cref="LogEventNames"/>. There is deliberately no catch-all value: an
/// unknown event must fail instead of being logged as "something happened".
/// </remarks>
public enum LogEvent
{
    /// <summary>The machine-wide single-instance lock was acquired.</summary>
    LockAcquired = 0,

    /// <summary>The single-instance lock handle was closed.</summary>
    LockReleased = 1,

    /// <summary>A leftover ownership record was taken over by a new owner.</summary>
    LockTakeover = 2,

    /// <summary>The session process is starting.</summary>
    SessionStarting = 3,

    /// <summary>The session is ready and accepts tool calls.</summary>
    SessionReady = 4,

    /// <summary>A protected desktop or a non-interactive session blocks the session.</summary>
    SessionBlocked = 5,

    /// <summary>The session entered the closing state.</summary>
    SessionClosing = 6,

    /// <summary>The pipe or the child session was lost and no automatic reconnect happens.</summary>
    SessionDisconnected = 7,

    /// <summary>The child-session system switch was turned on by this product.</summary>
    ChildSessionsEnabled = 8,

    /// <summary>The child-session system switch was restored to its previous value.</summary>
    ChildSessionsRestored = 9,

    /// <summary>A child-session agent process was spawned.</summary>
    AgentSpawned = 10,

    /// <summary>The host claimed a spawned agent.</summary>
    AgentClaimed = 11,

    /// <summary>An agent was not claimed within its startup window.</summary>
    AgentUnclaimed = 12,

    /// <summary>A child-session agent process exited.</summary>
    AgentDead = 13,

    /// <summary>A pipe peer failed the process or account check and was refused.</summary>
    IpcPeerRefused = 14,

    /// <summary>A pipe frame violated the protocol.</summary>
    IpcProtocolError = 15,

    /// <summary>A teardown step failed.</summary>
    TeardownFailed = 16,

    /// <summary>The startup gate selected an authentication tier.</summary>
    GateTierSelected = 17,

    /// <summary>The startup gate found no usable authentication tier and refused to start.</summary>
    GateTierNone = 18,
}

/// <summary>
/// The wire names of <see cref="LogEvent"/>. Every defined event maps to exactly one lowercase,
/// dot-separated name, and parsing an unknown name fails instead of falling back to a default event.
/// </summary>
public static class LogEventNames
{
    private static readonly FrozenDictionary<LogEvent, string> _wireNamesByEvent = CreateWireNamesByEvent();
    private static readonly FrozenDictionary<string, LogEvent> _eventsByWireName = CreateEventsByWireName(_wireNamesByEvent);

    /// <summary>Returns the contract wire name of an event.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="LogEvent"/>.</exception>
    public static string ToWireName(LogEvent logEvent)
    {
        if (!_wireNamesByEvent.TryGetValue(logEvent, out string? wireName))
        {
            throw new ArgumentOutOfRangeException(nameof(logEvent), logEvent, "Unknown log event.");
        }

        return wireName;
    }

    /// <summary>
    /// Tries to parse a wire name back into an event. A null or unknown name returns false; there is no
    /// fallback event, so the caller must not log the out value when this method returns false.
    /// </summary>
    public static bool TryFromWireName(string? wireName, out LogEvent logEvent)
    {
        if (wireName is null)
        {
            logEvent = default;
            return false;
        }

        return _eventsByWireName.TryGetValue(wireName, out logEvent);
    }

    private static FrozenDictionary<LogEvent, string> CreateWireNamesByEvent()
    {
        Dictionary<LogEvent, string> wireNames = new()
        {
            [LogEvent.LockAcquired] = "lock.acquired",
            [LogEvent.LockReleased] = "lock.released",
            [LogEvent.LockTakeover] = "lock.takeover",
            [LogEvent.SessionStarting] = "session.starting",
            [LogEvent.SessionReady] = "session.ready",
            [LogEvent.SessionBlocked] = "session.blocked",
            [LogEvent.SessionClosing] = "session.closing",
            [LogEvent.SessionDisconnected] = "session.disconnected",
            [LogEvent.ChildSessionsEnabled] = "childsessions.enabled",
            [LogEvent.ChildSessionsRestored] = "childsessions.restored",
            [LogEvent.AgentSpawned] = "agent.spawned",
            [LogEvent.AgentClaimed] = "agent.claimed",
            [LogEvent.AgentUnclaimed] = "agent.unclaimed",
            [LogEvent.AgentDead] = "agent.dead",
            [LogEvent.IpcPeerRefused] = "ipc.peer_refused",
            [LogEvent.IpcProtocolError] = "ipc.protocol_error",
            [LogEvent.TeardownFailed] = "teardown.failed",
            [LogEvent.GateTierSelected] = "gate.tier_selected",
            [LogEvent.GateTierNone] = "gate.tier_none",
        };

        if (wireNames.Count != Enum.GetValues<LogEvent>().Length)
        {
            throw new InvalidOperationException(
                "The log event wire names must cover every log event exactly once.");
        }

        return wireNames.ToFrozenDictionary();
    }

    private static FrozenDictionary<string, LogEvent> CreateEventsByWireName(
        FrozenDictionary<LogEvent, string> wireNamesByEvent)
    {
        Dictionary<string, LogEvent> events = new(StringComparer.Ordinal);
        foreach (KeyValuePair<LogEvent, string> pair in wireNamesByEvent)
        {
            if (!events.TryAdd(pair.Value, pair.Key))
            {
                throw new InvalidOperationException(
                    $"The log event wire name '{pair.Value}' is mapped more than once.");
            }
        }

        return events.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
