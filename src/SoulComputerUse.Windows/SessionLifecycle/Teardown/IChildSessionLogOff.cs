using System;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>Requests the logoff of one child session.</summary>
/// <remarks>
/// The port is only called when the plan contains a logoff step, so a session without a child session
/// never reaches it.
/// </remarks>
public interface IChildSessionLogOff
{
    /// <summary>Requests the logoff of one child session.</summary>
    /// <param name="wtsSessionId">The Windows session id of the child session; always positive.</param>
    /// <exception cref="Exception">The system refused the logoff request.</exception>
    void LogOff(int wtsSessionId);
}
