using System;
using System.Collections.Generic;
using System.Globalization;

namespace SoulComputerUse.Windows.Hosting.Startup.Phases;

/// <summary>One observer failure: the phase whose notification failed, and the failure itself.</summary>
public sealed record SessionPhaseFailure
{
    /// <summary>Creates one observer failure.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The phase is not a defined value.</exception>
    /// <exception cref="ArgumentNullException">The failure is null.</exception>
    public SessionPhaseFailure(SessionStartPhase phase, Exception failure)
    {
        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown session start phase.");
        }

        ArgumentNullException.ThrowIfNull(failure);

        Phase = phase;
        Failure = failure;
    }

    /// <summary>The phase whose notification failed.</summary>
    public SessionStartPhase Phase { get; }

    /// <summary>The failure the observer raised.</summary>
    public Exception Failure { get; }

    /// <summary>
    /// Renders one line for a diagnostic report: the phase's wire name and the failure's type, never the
    /// failure's message, which a caller-injected observer could fill with anything.
    /// </summary>
    public string Describe()
    {
        return "observer:" + SessionStartPhaseNames.ToWireName(Phase) + ":" + Failure.GetType().Name;
    }
}

/// <summary>
/// The complete result of one successful phase run: the phases the runner entered in contract order and
/// the observer failures it recorded instead of letting them stop the session.
/// </summary>
/// <remarks>
/// <para>
/// An observer is a reporting channel, not a gate: a caption that throws must not turn a session that is
/// really ready into a failed start. It is therefore recorded rather than propagated, which also keeps it
/// from being swallowed: the caller sees every failure in <see cref="Failures"/>.
/// </para>
/// <para>
/// The constructor refuses a phase list that leaves the contract order, repeats a phase or ends anywhere
/// but <see cref="SessionStartPhase.Ready"/>, because a report that claims readiness without reaching it
/// would be the one thing this layer exists to prevent.
/// </para>
/// </remarks>
public sealed record SessionPhaseReport
{
    /// <summary>Creates one report and refuses a phase list that contradicts the contract order.</summary>
    /// <exception cref="ArgumentNullException">The phase list, the failure list or the lock handle is null.</exception>
    /// <exception cref="ArgumentException">
    /// The list is empty, a phase repeats or leaves the order, the list does not end in ready, or a
    /// failure entry is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A phase is not a defined value.</exception>
    public SessionPhaseReport(
        IReadOnlyList<SessionStartPhase> phases,
        IReadOnlyList<SessionPhaseFailure> failures,
        IDisposable lockHandle)
    {
        ArgumentNullException.ThrowIfNull(phases);
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(lockHandle);

        if (phases.Count == 0)
        {
            throw new ArgumentException("A phase report needs at least one phase.", nameof(phases));
        }

        SessionStartPhase[] copiedPhases = new SessionStartPhase[phases.Count];
        int previousRank = -1;
        for (int index = 0; index < phases.Count; index++)
        {
            SessionStartPhase phase = phases[index];
            if (!Enum.IsDefined(phase))
            {
                throw new ArgumentOutOfRangeException(nameof(phases), phase, "Unknown session start phase.");
            }

            int rank = (int)phase;
            if (rank <= previousRank)
            {
                throw new ArgumentException(
                    "The phases of a report must follow the contract order without repeating a phase.",
                    nameof(phases));
            }

            copiedPhases[index] = phase;
            previousRank = rank;
        }

        if (copiedPhases[^1] != SessionStartPhase.Ready)
        {
            throw new ArgumentException(
                "A phase report only describes a run that reached ready.",
                nameof(phases));
        }

        SessionPhaseFailure[] copiedFailures = new SessionPhaseFailure[failures.Count];
        for (int index = 0; index < failures.Count; index++)
        {
            copiedFailures[index] = failures[index]
                ?? throw new ArgumentException("A phase report cannot carry a null failure.", nameof(failures));
        }

        Phases = Array.AsReadOnly(copiedPhases);
        Failures = Array.AsReadOnly(copiedFailures);
        LockHandle = lockHandle;
    }

    /// <summary>The phases this run entered, in contract order, ending in ready.</summary>
    public IReadOnlyList<SessionStartPhase> Phases { get; }

    /// <summary>The observer failures recorded during the run, in the order they happened.</summary>
    public IReadOnlyList<SessionPhaseFailure> Failures { get; }

    /// <summary>
    /// The lock handle the before-connect segment acquired. A ready session holds the machine-wide lock
    /// for its whole life, so the handle travels to the caller instead of being closed here.
    /// </summary>
    public IDisposable LockHandle { get; }

    /// <summary>Whether every observer notification succeeded.</summary>
    public bool ObserversSucceeded => Failures.Count == 0;

    /// <summary>
    /// Renders the phases and the observer failures as one line, using wire names and failure type names
    /// only, so a report line cannot leak a path, an account, a SID or a nonce.
    /// </summary>
    public string Describe()
    {
        List<string> parts = new(Phases.Count);
        foreach (SessionStartPhase phase in Phases)
        {
            parts.Add(SessionStartPhaseNames.ToWireName(phase));
        }

        string phases = string.Join(">", parts);
        if (Failures.Count == 0)
        {
            return "phases=" + phases;
        }

        List<string> failureParts = new(Failures.Count);
        foreach (SessionPhaseFailure failure in Failures)
        {
            failureParts.Add(failure.Describe());
        }

        return "phases="
            + phases
            + " failures="
            + Failures.Count.ToString(CultureInfo.InvariantCulture)
            + "("
            + string.Join(";", failureParts)
            + ")";
    }
}
