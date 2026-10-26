namespace SoulComputerUse.Core.Session;

/// <summary>
/// The seven session states from the interface contract. The values are the contract's states,
/// and the wire names come from <see cref="SessionStateNames"/>; nothing else may invent a state.
/// </summary>
public enum SessionState
{
    /// <summary>The process is starting up and has not acquired the lock yet.</summary>
    Starting = 0,

    /// <summary>Waiting for the operator to finish the system verification step themselves.</summary>
    AwaitingCredentials = 1,

    /// <summary>The session exists and the agent handshake is in progress.</summary>
    Connecting = 2,

    /// <summary>The session accepts tool calls.</summary>
    Ready = 3,

    /// <summary>The pipe or the session was lost; no automatic reconnect.</summary>
    Disconnected = 4,

    /// <summary>A protected desktop or a missing prerequisite blocks the session.</summary>
    Blocked = 5,

    /// <summary>The session is being torn down.</summary>
    Closing = 6,
}