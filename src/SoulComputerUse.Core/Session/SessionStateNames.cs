using System;

namespace SoulComputerUse.Core.Session;

/// <summary>
/// The wire names of the session states. They are part of the interface contract, so they are
/// defined once here instead of being repeated at every serialization site.
/// </summary>
public static class SessionStateNames
{
    /// <summary>Returns the contract wire name of a state.</summary>
    public static string ToWireName(SessionState state)
    {
        return state switch
        {
            SessionState.Starting => "starting",
            SessionState.AwaitingCredentials => "awaiting-credentials",
            SessionState.Connecting => "connecting",
            SessionState.Ready => "ready",
            SessionState.Disconnected => "disconnected",
            SessionState.Blocked => "blocked",
            SessionState.Closing => "closing",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown session state."),
        };
    }

    /// <summary>Parses a contract wire name back into a state.</summary>
    public static SessionState FromWireName(string wireName)
    {
        ArgumentNullException.ThrowIfNull(wireName);
        return wireName switch
        {
            "starting" => SessionState.Starting,
            "awaiting-credentials" => SessionState.AwaitingCredentials,
            "connecting" => SessionState.Connecting,
            "ready" => SessionState.Ready,
            "disconnected" => SessionState.Disconnected,
            "blocked" => SessionState.Blocked,
            "closing" => SessionState.Closing,
            _ => throw new ArgumentOutOfRangeException(nameof(wireName), wireName, "Unknown session state wire name."),
        };
    }
}