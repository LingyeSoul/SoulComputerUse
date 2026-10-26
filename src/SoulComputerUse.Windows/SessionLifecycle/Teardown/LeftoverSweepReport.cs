using System;
using System.Collections.Generic;

namespace SoulComputerUse.Windows.SessionLifecycle.Teardown;

/// <summary>The outcome of one leftover sweep.</summary>
/// <remarks>
/// Both lists are immutable snapshots taken when the sweep finished. A returned report always has an
/// empty <see cref="RemainingSessionIds"/>, because a survivor turns the sweep into a refusal; the
/// refusal carries its attempt's report under <see cref="LeftoverSweeper.ReportDataKey"/>, so the ids
/// stay available without parsing the message.
/// </remarks>
public sealed record LeftoverSweepReport
{
    /// <summary>Copies the two id lists.</summary>
    /// <param name="loggedOffSessionIds">The child sessions the sweep asked the system to log off, in sweep order.</param>
    /// <param name="remainingSessionIds">The logged-off sessions the re-read still found; empty unless this report travels with a refusal.</param>
    /// <exception cref="ArgumentNullException">A list is null.</exception>
    public LeftoverSweepReport(
        IReadOnlyList<int> loggedOffSessionIds,
        IReadOnlyList<int> remainingSessionIds)
    {
        ArgumentNullException.ThrowIfNull(loggedOffSessionIds);
        ArgumentNullException.ThrowIfNull(remainingSessionIds);

        LoggedOffSessionIds = loggedOffSessionIds;
        RemainingSessionIds = remainingSessionIds;
    }

    /// <summary>The child sessions the sweep asked the system to log off, in sweep order.</summary>
    public IReadOnlyList<int> LoggedOffSessionIds { get; }

    /// <summary>The logged-off sessions the re-read still found; empty unless this report travels with a refusal.</summary>
    public IReadOnlyList<int> RemainingSessionIds { get; }
}
