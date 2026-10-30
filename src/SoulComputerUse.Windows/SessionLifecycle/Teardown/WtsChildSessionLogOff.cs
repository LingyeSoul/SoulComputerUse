using System;
using SoulComputerUse.Windows.Rdp;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>
/// Logs one child session off through the machine's session table, after proving at the last moment
/// that the id still names a child session of this product.
/// </summary>
/// <remarks>
/// The id a teardown carries was recorded when the child session was created or attached; the session
/// table is the only authority on what that id names now, so the port re-reads the table immediately
/// before the logoff request and refuses to send it unless the current entry is still provably this
/// product's own child session. A child session that logged itself off mid-run frees its id for reuse
/// by an innocent session — a fast user switch or an inbound remote desktop — and a teardown that
/// trusted its recorded id alone would sign that innocent session off, which cannot be undone.
/// <para>
/// The proof is <see cref="LeftoverChildSessionPolicy.ClassifyOwnedTarget"/> over
/// <see cref="LeftoverEvidence"/> built from the id being logged off and the account this process runs
/// as, and <see cref="LogOffTargetGuard"/> over the currently active console session id. An id that has
/// disappeared from the table has already met the logoff's goal and returns as a success without a
/// system call. Everything the table cannot prove ours — the service session, the current console
/// session, a session of another account, a session whose account the system did not answer for — is
/// refused with an exception before the system is called. The port never sends a request it could not
/// prove.
/// </para>
/// <para>
/// This port is the only sanctioned route from a teardown to <see cref="IWtsSessionTable.LogOff"/>: the
/// session table itself is a generic machine port and must not know the product's policy, while every
/// caller that names a child session goes through here, so no second route can skip this re-check.
/// </para>
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
    /// <exception cref="InvalidOperationException">
    /// The entry the table returns for the id is not provably a child session of this product — it is the
    /// current console session, the service session, or a session of another or unreadable account — so
    /// the request is refused before the system is called.
    /// </exception>
    public void LogOff(int wtsSessionId)
    {
        if (wtsSessionId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(wtsSessionId),
                wtsSessionId,
                "Only a positive Windows session id can be a child session of this product.");
        }

        WtsSessionEntry? target = null;
        foreach (WtsSessionEntry entry in _sessionTable.Read())
        {
            if (entry.Id == wtsSessionId)
            {
                target = entry;
                break;
            }
        }

        // The goal of the request is that this session stops existing; a table that no longer lists the
        // id says the goal is already met, so returning without a system call is a success, not a skip.
        if (target is null)
        {
            return;
        }

        int consoleSessionId = _sessionTable.ActiveConsoleSessionId();
        LogOffTargetGuard.EnsureRemovable(wtsSessionId, consoleSessionId);

        LeftoverEvidence evidence = new(wtsSessionId, _sessionTable.CurrentSessionAccountName());
        SessionCandidate candidate = new(target.Id, target.StationName, target.State, target.AccountName);
        if (LeftoverChildSessionPolicy.ClassifyOwnedTarget(candidate, consoleSessionId, evidence) != LeftoverProof.Ours)
        {
            throw new InvalidOperationException(
                $"The teardown refused to log off session {wtsSessionId}: the session table no longer proves it to be a child session of this product, so the logoff request was not sent.");
        }

        _sessionTable.LogOff(wtsSessionId);
    }
}
