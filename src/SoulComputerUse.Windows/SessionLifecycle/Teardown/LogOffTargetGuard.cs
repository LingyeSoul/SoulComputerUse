using System;
using SoulComputerUse.Windows.Rdp;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>
/// The last check before a session is signed off: the target must not be the service session, the
/// operator's console session, or a non-positive id that names no session.
/// </summary>
/// <remarks>
/// <see cref="LeftoverChildSessionPolicy"/> already excludes those sessions, so this guard is a second
/// line of defence rather than the primary rule. It stays internal because it is only meaningful to the
/// sweep: a caller outside this directory must not be able to log a session off at all.
/// </remarks>
internal static class LogOffTargetGuard
{
    /// <summary>Refuses a target that must never be signed off.</summary>
    /// <param name="sessionId">The session the sweep is about to log off.</param>
    /// <param name="activeConsoleSessionId">The session attached to the operator's console.</param>
    /// <exception cref="InvalidOperationException">The target is the service session, the console session, or not a session id.</exception>
    internal static void EnsureRemovable(int sessionId, int activeConsoleSessionId)
    {
        if (sessionId == LeftoverChildSessionPolicy.ServiceSessionId)
        {
            throw new InvalidOperationException(
                "The sweep refused to log off session zero, which is the service session.");
        }

        if (sessionId == activeConsoleSessionId)
        {
            throw new InvalidOperationException(
                "The sweep refused to log off the active console session, which is the operator's own desktop.");
        }

        if (sessionId < 0)
        {
            throw new InvalidOperationException(
                "The sweep refused to log off a negative session id, which names no session.");
        }
    }
}
