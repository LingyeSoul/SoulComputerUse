using System;
using System.Collections.Generic;
using SoulComputerUse.Windows.Rdp;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>
/// Executes the pre-connection leftover sweep: read the session table, decide which sessions are child
/// sessions of this product, request a logoff for each, and prove by re-reading the table that every
/// requested session is gone.
/// </summary>
/// <remarks>
/// <see cref="LeftoverChildSessionPolicy"/> decides what a leftover is. This type only carries that
/// decision out, and it refuses to log off anything outside the decision: a session id equal to the
/// service session or to the active console session is rejected with an
/// <see cref="InvalidOperationException"/> before any system call, because signing the operator out of
/// their own desktop is unrecoverable.
/// <para>
/// A logoff request is a request, not a result. When the request is refused, or when the re-read still
/// shows a requested session, the sweep raises
/// <see cref="LeftoverChildSessionPolicy.ManualInterventionRequired"/>: connecting anyway would present
/// a session that never asked the operator to log on.
/// </para>
/// <para>
/// The sweep writes no log entries. Its message names session ids and counts only, never a station
/// name, a path or an account identifier.
/// </para>
/// </remarks>
public sealed class LeftoverSweeper
{
    /// <summary>
    /// The <see cref="Exception.Data"/> key under which a refusal carries the attempt's
    /// <see cref="LeftoverSweepReport"/>. The exception type is pinned to
    /// <see cref="InvalidOperationException"/> by
    /// <see cref="LeftoverChildSessionPolicy.ManualInterventionRequired"/>, so the report travels beside
    /// the message instead of in a subtype.
    /// </summary>
    public const string ReportDataKey = "SoulComputerUse.SessionLifecycle.Teardown.LeftoverSweepReport";

    /// <summary>How often the session table is read while a logoff request settles.</summary>
    private static readonly TimeSpan ConfirmationInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long a logoff request may take to disappear from the session table. Windows finishes the logoff
    /// asynchronously, and a session that is still listed a moment after the request is not a failure.
    /// </summary>
    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(15);

    private readonly IWtsSessionTable _sessionTable;
    private readonly ILogoffSettle _settle;

    /// <summary>Creates a sweep over one session table and the wait its confirmations use.</summary>
    /// <exception cref="ArgumentNullException">The session table or the settle is null.</exception>
    public LeftoverSweeper(IWtsSessionTable sessionTable, ILogoffSettle settle)
    {
        ArgumentNullException.ThrowIfNull(sessionTable);
        ArgumentNullException.ThrowIfNull(settle);
        _sessionTable = sessionTable;
        _settle = settle;
    }

    /// <summary>
    /// Logs off every leftover child session and confirms that each of them is gone.
    /// </summary>
    /// <param name="evidence">What the policy proves with: the taken-over record's child session id and this process's account.</param>
    /// <returns>The ids the sweep logged off; both lists are empty when nothing was left over.</returns>
    /// <exception cref="InvalidOperationException">
    /// A session is neither provably ours nor provably somebody else's, a target is the service or the
    /// console session, a logoff request was refused, or a requested session survived the request. A
    /// refusal carries the attempt's report under <see cref="ReportDataKey"/>.
    /// </exception>
    /// <exception cref="System.ComponentModel.Win32Exception">The session table could not be read.</exception>
    public LeftoverSweepReport Sweep(LeftoverEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        int consoleSessionId = _sessionTable.ActiveConsoleSessionId();
        List<SessionCandidate> candidates = [];
        foreach (WtsSessionEntry entry in _sessionTable.Read())
        {
            candidates.Add(new SessionCandidate(entry.Id, entry.StationName, entry.State, entry.AccountName));
        }

        IReadOnlyList<SessionCandidate> leftovers = LeftoverChildSessionPolicy.FindLeftovers(candidates, consoleSessionId, evidence);
        IReadOnlyList<SessionCandidate> unproven = LeftoverChildSessionPolicy.FindUnproven(candidates, consoleSessionId, evidence);

        // The undecided sessions stop the sweep before its first logoff request: logging anything off
        // while one of them exists would decide a question the evidence could not, and the operator who
        // has to look at the machine must see the machine exactly as the sweep found it.
        if (unproven.Count > 0)
        {
            throw Refuse(
                DescribeSessions(unproven) + " cannot be proven to be this product's leftovers or another person's sessions, so the sweep logs nothing off.",
                new LeftoverSweepReport(Array.Empty<int>(), Array.Empty<int>()));
        }

        if (leftovers.Count == 0)
        {
            return new LeftoverSweepReport(Array.Empty<int>(), Array.Empty<int>());
        }

        List<int> loggedOff = new(leftovers.Count);
        foreach (SessionCandidate leftover in leftovers)
        {
            // The console session id is re-read before every logoff request: a fast user switch between
            // two requests changes who sits on the console, and a guard that checks a stale snapshot
            // would clear a target that has become the operator's own desktop in the meantime.
            LogOffTargetGuard.EnsureRemovable(leftover.Id, _sessionTable.ActiveConsoleSessionId());
            try
            {
                _sessionTable.LogOff(leftover.Id);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                throw Refuse(
                    $"the logoff request for child session id {leftover.Id} failed, so the session may still be logged on.",
                    new LeftoverSweepReport(loggedOff.AsReadOnly(), Array.Empty<int>()));
            }

            loggedOff.Add(leftover.Id);
        }

        // A request is not a result: every requested session has to be absent from a fresh read before
        // a connection may present the logon screen. The read is repeated while the logoff settles,
        // because Windows removes the session asynchronously.
        List<int> freshSurvivors = FindSurvivors(loggedOff);
        int attempts = 0;
        while (freshSurvivors.Count > 0 && attempts * ConfirmationInterval < ConfirmationTimeout)
        {
            attempts++;
            _settle.Wait(ConfirmationInterval);
            freshSurvivors = FindSurvivors(loggedOff);
        }

        if (freshSurvivors.Count > 0)
        {
            throw Refuse(
                $"{freshSurvivors.Count} child session(s) survived the logoff request: session id(s) {string.Join(", ", freshSurvivors)}.",
                new LeftoverSweepReport(loggedOff.AsReadOnly(), freshSurvivors.AsReadOnly()));
        }

        return new LeftoverSweepReport(loggedOff.AsReadOnly(), Array.Empty<int>());
    }

    /// <summary>
    /// Names sessions by count and id only. The sweep's messages carry neither a station name nor an
    /// account name, so a refusal can be pasted into a report without identifying anybody.
    /// </summary>
    private static string DescribeSessions(IReadOnlyList<SessionCandidate> sessions)
    {
        List<string> ids = new(sessions.Count);
        foreach (SessionCandidate session in sessions)
        {
            ids.Add(session.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return sessions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " session(s) (session id(s) "
            + string.Join(", ", ids)
            + ")";
    }

    /// <summary>
    /// Re-reads the table and returns the requested ids that are still present, in request order. A
    /// session that answers to the same id is the same session, so presence is the whole check.
    /// </summary>
    private List<int> FindSurvivors(IReadOnlyList<int> requestedIds)
    {
        IReadOnlyList<WtsSessionEntry> entries = _sessionTable.Read();
        List<int> survivors = [];
        foreach (int requestedId in requestedIds)
        {
            foreach (WtsSessionEntry entry in entries)
            {
                if (entry.Id == requestedId)
                {
                    survivors.Add(requestedId);
                    break;
                }
            }
        }

        return survivors;
    }

    private static InvalidOperationException Refuse(string reason, LeftoverSweepReport report)
    {
        InvalidOperationException refusal = LeftoverChildSessionPolicy.ManualInterventionRequired(reason);
        refusal.Data[ReportDataKey] = report;
        return refusal;
    }
}
