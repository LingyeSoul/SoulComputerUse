using System;

namespace SoulComputerUse.Windows.Ipc.Agent;

/// <summary>
/// The timings the agent pipe client uses. Every value is supplied by the caller so a test can run
/// the contract's timeouts at a small scale without changing production values.
/// </summary>
public sealed record AgentClientTimings
{
    /// <summary>Creates the timings and refuses a non-positive or contradictory value.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is not positive, or the check interval is longer than the silence timeout.</exception>
    public AgentClientTimings(
        TimeSpan connectTimeout,
        TimeSpan handshakeTimeout,
        TimeSpan heartbeatSilenceTimeout,
        TimeSpan heartbeatCheckInterval,
        TimeSpan actionTimeout,
        TimeSpan screenshotTimeout)
    {
        RequirePositive(connectTimeout, nameof(connectTimeout));
        RequirePositive(handshakeTimeout, nameof(handshakeTimeout));
        RequirePositive(heartbeatSilenceTimeout, nameof(heartbeatSilenceTimeout));
        RequirePositive(heartbeatCheckInterval, nameof(heartbeatCheckInterval));
        RequirePositive(actionTimeout, nameof(actionTimeout));
        RequirePositive(screenshotTimeout, nameof(screenshotTimeout));

        if (heartbeatCheckInterval > heartbeatSilenceTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(heartbeatCheckInterval),
                heartbeatCheckInterval,
                "The heartbeat check interval must not be longer than the silence timeout.");
        }

        ConnectTimeout = connectTimeout;
        HandshakeTimeout = handshakeTimeout;
        HeartbeatSilenceTimeout = heartbeatSilenceTimeout;
        HeartbeatCheckInterval = heartbeatCheckInterval;
        ActionTimeout = actionTimeout;
        ScreenshotTimeout = screenshotTimeout;
    }

    /// <summary>The maximum time the client waits for the agent pipe to accept a connection.</summary>
    public TimeSpan ConnectTimeout { get; }

    /// <summary>The maximum time the client waits for the hello event and the hello_ack response.</summary>
    public TimeSpan HandshakeTimeout { get; }

    /// <summary>The interval without an event or response after which the agent is presumed lost.</summary>
    public TimeSpan HeartbeatSilenceTimeout { get; }

    /// <summary>The interval at which the client rechecks the silence timeout.</summary>
    public TimeSpan HeartbeatCheckInterval { get; }

    /// <summary>The contract timeout for action requests.</summary>
    public TimeSpan ActionTimeout { get; }

    /// <summary>The contract timeout for screenshot requests.</summary>
    public TimeSpan ScreenshotTimeout { get; }

    /// <summary>The contract timings: a three second handshake, a fifteen second silence limit, ten and fifteen second requests.</summary>
    public static AgentClientTimings Contract { get; } = new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15));

    private static void RequirePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The timing must be positive.");
        }
    }
}