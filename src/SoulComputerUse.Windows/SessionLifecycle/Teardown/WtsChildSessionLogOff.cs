using System;
using SoulComputerUse.Windows.SessionLifecycle;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>Logs one child session off through the machine's session table.</summary>
/// <remarks>
/// The session id is validated here as well as in the table so a non-positive id never reaches the
/// system call through this port.
/// </remarks>
public sealed class WtsChildSessionLogOff : IChildSessionLogOff
{
    private readonly IWtsSessionTable _sessionTable;

    /// <summary>Creates the port over a session table.</summary>
    /// <exception cref="ArgumentNullException">The session table is null.</exception>
    public WtsChildSessionLogOff(IWtsSessionTable sessionTable)
    {
        ArgumentNullException.ThrowIfNull(sessionTable);
        _sessionTable = sessionTable;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The id is zero or negative, so it names no child session.</exception>
    public void LogOff(int wtsSessionId)
    {
        if (wtsSessionId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(wtsSessionId),
                wtsSessionId,
                "Only a positive Windows session id can be a child session of this product.");
        }

        _sessionTable.LogOff(wtsSessionId);
    }
}
