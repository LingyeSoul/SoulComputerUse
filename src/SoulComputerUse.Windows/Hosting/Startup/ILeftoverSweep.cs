using System;
using SoulComputerUse.Windows.SessionLifecycle.Teardown;

namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>
/// The pre-connection leftover sweep as a port. It logs off every child session this product left
/// behind and proves each of them is gone; a refusal is a thrown exception, never a warning.
/// </summary>
/// <remarks>
/// The port is the only way the startup pipeline reaches the sweep, and the sweep is the only way any
/// connection may be preceded. The runner therefore never asks a connector to connect before this port
/// has returned a report.
/// </remarks>
public interface ILeftoverSweep
{
    /// <summary>Logs off every leftover child session and confirms that none survived.</summary>
    /// <exception cref="Exception">
    /// The table could not be read, a logoff request was refused, or a requested session survived. The
    /// exception may carry the attempt's report under <see cref="LeftoverSweeper.ReportDataKey"/>.
    /// </exception>
    LeftoverSweepReport Sweep();
}
