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
    /// <returns>The ids the sweep logged off; both lists are empty when nothing was left over.</returns>
    /// <exception cref="InvalidOperationException">
    /// A target is the service or the console session, a logoff request was refused, or a requested
    /// session survived the request. A refusal carries the attempt's report under
    /// <see cref="ReportDataKey"/>.
    /// </exception>
    /// <exception cref="System.ComponentModel.Win32Exception">The session table could not be read.</exception>
    public LeftoverSweepReport Sweep()
    {
        int consoleSessionId = _sessionTable.ActiveConsoleSessionId();
        IReadOnlyList<SessionCandidate> leftovers = FindLeftovers(_sessionTable.Read(), consoleSessionId);
        if (leftovers.Count == 0)
        {
            return new LeftoverSweepReport(Array.Empty<int>(), Array.Empty<int>());
        }

        List<int> loggedOff = new(leftovers.Count);
        foreach (SessionCandidate leftover in leftovers)
        {
            LogOffTargetGuard.EnsureRemovable(leftover.Id, consoleSessionId);
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

    /// <summary>Maps table entries onto the policy's candidate shape.</summary>
    private static IReadOnlyList<SessionCandidate> FindLeftovers(
        IReadOnlyList<WtsSessionEntry> entries,
        int consoleSessionId)
    {
        List<SessionCandidate> candidates = new(entries.Count);
        foreach (WtsSessionEntry entry in entries)
        {
            candidates.Add(new SessionCandidate(entry.Id, entry.StationName, entry.State));
        }

        return LeftoverChildSessionPolicy.FindLeftovers(candidates, consoleSessionId);
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
