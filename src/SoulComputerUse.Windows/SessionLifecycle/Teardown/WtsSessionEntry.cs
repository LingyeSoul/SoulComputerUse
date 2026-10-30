using System;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>One entry of the machine's session table, read without changing anything.</summary>
public sealed record WtsSessionEntry
{
    /// <summary>Creates one entry.</summary>
    /// <param name="id">The Windows Terminal Services session id; zero is the service session.</param>
    /// <param name="stationName">The window station the session is attached to, as the system reports it.</param>
    /// <param name="state">The raw <c>WTS_CONNECTSTATE_CLASS</c> value; the teardown layer never reinterprets it.</param>
    /// <param name="accountName">
    /// The account name the session is logged on as, or null when the system did not answer for this
    /// session. An empty string is a readable answer that names no account. The name is compared by the
    /// leftover policy and never reported.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// The station name is null. The policy classifies a session by its station name, so an entry without
    /// one could not be judged and must not enter the table. A null account name, by contrast, is a real
    /// observation: the system may refuse to answer for another account's session, and the policy treats
    /// that as absent proof.
    /// </exception>
    public WtsSessionEntry(int id, string stationName, int state, string? accountName = null)
    {
        ArgumentNullException.ThrowIfNull(stationName);

        Id = id;
        StationName = stationName;
        State = state;
        AccountName = accountName;
    }

    /// <summary>The Windows session id; zero is the service session.</summary>
    public int Id { get; }

    /// <summary>The window station the session is attached to, as the system reports it.</summary>
    public string StationName { get; }

    /// <summary>The raw <c>WTS_CONNECTSTATE_CLASS</c> value; the teardown layer never reinterprets it.</summary>
    public int State { get; }

    /// <summary>
    /// The account name this session is logged on as; null when the system did not answer for this
    /// session, and empty when it answered that no account is logged on.
    /// </summary>
    public string? AccountName { get; }
}
