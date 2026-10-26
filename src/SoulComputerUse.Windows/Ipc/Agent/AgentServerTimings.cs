using System;

namespace SoulComputerUse.Windows.Ipc.Agent;

/// <summary>
/// The timings the agent pipe server uses. Every value is supplied by the caller so a test can run
/// the contract's handshake and heartbeat rules at a small scale.
/// </summary>
public sealed record AgentServerTimings
{
    /// <summary>Creates the timings and refuses a non-positive value.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is not positive.</exception>
    public AgentServerTimings(TimeSpan handshakeTimeout, TimeSpan heartbeatInterval)
    {
        RequirePositive(handshakeTimeout, nameof(handshakeTimeout));
        RequirePositive(heartbeatInterval, nameof(heartbeatInterval));
        HandshakeTimeout = handshakeTimeout;
        HeartbeatInterval = heartbeatInterval;
    }

    /// <summary>The maximum time the server waits for the host's <c>hello_ack</c>.</summary>
    public TimeSpan HandshakeTimeout { get; }

    /// <summary>The interval between server heartbeat events after the handshake.</summary>
    public TimeSpan HeartbeatInterval { get; }

    /// <summary>The contract timings: a three second handshake and a five second heartbeat.</summary>
    public static AgentServerTimings Contract { get; } = new(
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5));

    private static void RequirePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The timing must be positive.");
        }
    }
}