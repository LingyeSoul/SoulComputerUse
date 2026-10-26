using System;

namespace SoulComputerUse.Windows.Ipc.Agent;

/// <summary>
/// The names of the host to child-session agent pipes. A name is derived only from a validated
/// session id, so a caller cannot smuggle a path, a separator or another pipe into the transport.
/// </summary>
public static class AgentPipeNames
{
    /// <summary>The fixed prefix of every agent pipe from section 4 of the interface contract.</summary>
    public const string Prefix = "SoulComputerUse.agent.";

    /// <summary>
    /// Returns <c>SoulComputerUse.agent.{sessionId}</c> for one session id in the D UUID format.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The value is empty, whitespace, or not a UUID in the D format. A rejected value is never
    /// embedded in the pipe name.
    /// </exception>
    public static string ForSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (!Guid.TryParseExact(sessionId, "D", out Guid parsed))
        {
            throw new ArgumentException(
                "The session id must be a UUID in the hyphenated D format; the pipe name is never derived from a raw string.",
                nameof(sessionId));
        }

        return Prefix + parsed.ToString("D");
    }
}