using System;
using SoulComputerUse.Windows.SessionLifecycle.Teardown;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>
/// The outcome of the pre-connection sweep: whether it finished, and the report it produced. A failed
/// sweep carries the attempt's report when the sweep layer attached one, so the operator can still see
/// how far it got.
/// </summary>
public sealed record LeftoverSweepOutcome
{
    private LeftoverSweepOutcome(bool succeeded, LeftoverSweepReport? report)
    {
        Succeeded = succeeded;
        Report = report;
    }

    /// <summary>Whether the sweep proved that no leftover child session is logged on.</summary>
    public bool Succeeded { get; }

    /// <summary>The sweep's report, or null when the sweep failed before it could report.</summary>
    public LeftoverSweepReport? Report { get; }

    /// <summary>Creates a successful outcome around the sweep's report.</summary>
    /// <exception cref="ArgumentNullException">The report is null.</exception>
    public static LeftoverSweepOutcome Completed(LeftoverSweepReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.RemainingSessionIds.Count != 0)
        {
            throw new ArgumentException(
                "A successful sweep cannot leave a requested session behind.",
                nameof(report));
        }

        return new LeftoverSweepOutcome(true, report);
    }

    /// <summary>Creates a failed outcome, with the attempt's report when one exists.</summary>
    public static LeftoverSweepOutcome Failed(LeftoverSweepReport? report)
    {
        return new LeftoverSweepOutcome(false, report);
    }
}
