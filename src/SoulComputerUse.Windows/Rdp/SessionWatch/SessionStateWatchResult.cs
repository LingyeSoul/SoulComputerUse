using System;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Rdp.SessionWatch;

/// <summary>
/// The outcome of a watch that reached the ready state: the state it settled on, what the observed
/// chain proves about the logon step, and how long the watch took.
/// </summary>
/// <param name="FinalState">The state the watch settled on, which is ready on success.</param>
/// <param name="Observation">What the observed chain proves about the operator's logon step.</param>
/// <param name="Elapsed">The monotonic time the watch took from its start to the ready state.</param>
public sealed record SessionStateWatchResult(
    SessionState FinalState,
    LogonObservation Observation,
    TimeSpan Elapsed);