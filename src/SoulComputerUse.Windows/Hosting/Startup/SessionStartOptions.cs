using System;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>
/// The identity values the ownership record carries. They are supplied once so the runner never reads
/// the clock or the process table twice, and a record written at any stage describes the same start.
/// </summary>
public sealed record SessionStartOptions
{
    /// <summary>Creates the options and refuses values a record cannot carry.</summary>
    /// <exception cref="ArgumentException">The session id or the times are unusable.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The host process id is not positive.</exception>
    public SessionStartOptions(
        string sessionId,
        int hostPid,
        DateTimeOffset hostProcessStartTime,
        DateTimeOffset acquiredAt)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("The session id must not be empty.", nameof(sessionId));
        }

        if (!Guid.TryParseExact(sessionId, "D", out Guid sessionGuid))
        {
            throw new ArgumentException("The session id must be a D-format UUID.", nameof(sessionId));
        }

        if (hostPid <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hostPid), hostPid, "The host process id must be positive.");
        }

        if (hostProcessStartTime == default)
        {
            throw new ArgumentException(
                "The host process start time must be a real timestamp, not the default value.",
                nameof(hostProcessStartTime));
        }

        if (acquiredAt == default)
        {
            throw new ArgumentException(
                "The acquisition time must be a real timestamp, not the default value.",
                nameof(acquiredAt));
        }

        SessionId = sessionGuid.ToString("D");
        HostPid = hostPid;
        HostProcessStartTime = hostProcessStartTime;
        AcquiredAt = acquiredAt;
    }

    /// <summary>The UUIDv4 session id the agent, the owner pipe and the record share.</summary>
    public string SessionId { get; }

    /// <summary>The owner process id, which the recovery rules check for liveness.</summary>
    public int HostPid { get; }

    /// <summary>The owner process start time, which the recovery rules use to detect PID reuse.</summary>
    public DateTimeOffset HostProcessStartTime { get; }

    /// <summary>When this process took the lock; the twenty-four hour staleness rule uses it.</summary>
    public DateTimeOffset AcquiredAt { get; }
}
