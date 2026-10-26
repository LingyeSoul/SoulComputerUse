using System;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>One entry of the machine's session table, read without changing anything.</summary>
public sealed record WtsSessionEntry
{
    /// <summary>Creates one entry.</summary>
    /// <param name="id">The Windows Terminal Services session id; zero is the service session.</param>
    /// <param name="stationName">The window station the session is attached to, as the system reports it.</param>
    /// <param name="state">The raw <c>WTS_CONNECTSTATE_CLASS</c> value; the teardown layer never reinterprets it.</param>
    /// <exception cref="ArgumentNullException">
    /// The station name is null. The policy classifies a session by its station name, so an entry without
    /// one could not be judged and must not enter the table.
    /// </exception>
    public WtsSessionEntry(int id, string stationName, int state)
    {
        ArgumentNullException.ThrowIfNull(stationName);

        Id = id;
        StationName = stationName;
        State = state;
    }

    /// <summary>The Windows session id; zero is the service session.</summary>
    public int Id { get; }

    /// <summary>The window station the session is attached to, as the system reports it.</summary>
    public string StationName { get; }

    /// <summary>The raw <c>WTS_CONNECTSTATE_CLASS</c> value; the teardown layer never reinterprets it.</summary>
    public int State { get; }
}
