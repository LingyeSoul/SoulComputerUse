using System.Collections.Generic;
using System.ComponentModel;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>
/// The session table operations the teardown layer needs.
/// </summary>
/// <remarks>
/// The interface exists so the leftover sweep can be exercised without a real logoff: a test must never
/// sign a session off on the machine that runs it.
/// </remarks>
public interface IWtsSessionTable
{
    /// <summary>Enumerates every session on the machine, including the service and console sessions.</summary>
    /// <returns>An immutable snapshot; a later change to the machine does not alter it.</returns>
    /// <exception cref="Win32Exception">The system refused to enumerate the sessions.</exception>
    IReadOnlyList<WtsSessionEntry> Read();

    /// <summary>Requests the logoff of one session.</summary>
    /// <exception cref="System.ArgumentOutOfRangeException">The id is zero or negative.</exception>
    /// <exception cref="Win32Exception">The system refused the logoff request.</exception>
    void LogOff(int sessionId);

    /// <summary>Returns the session currently attached to the interactive console.</summary>
    /// <exception cref="System.InvalidOperationException">No session is attached to the console.</exception>
    int ActiveConsoleSessionId();
}
