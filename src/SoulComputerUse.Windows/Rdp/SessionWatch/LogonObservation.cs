using System;
using System.Collections.Generic;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Rdp.SessionWatch;

/// <summary>
/// What the observed state chain proves about the operator's logon step. The chain is the sequence of
/// distinct states the client really reported, in order, and the flags are derived from it, so an
/// upper layer can tell a session that showed the logon screen from one that skipped it.
/// </summary>
public sealed record LogonObservation
{
    private LogonObservation(
        bool awaitingCredentialsObserved,
        IReadOnlyList<SessionState> chain,
        bool directToReady)
    {
        AwaitingCredentialsObserved = awaitingCredentialsObserved;
        Chain = chain;
        DirectToReady = directToReady;
    }

    /// <summary>True when the chain ever reported the awaiting-credentials state.</summary>
    public bool AwaitingCredentialsObserved { get; }

    /// <summary>A read-only copy of the observed states, in the order the client reported them.</summary>
    public IReadOnlyList<SessionState> Chain { get; }

    /// <summary>
    /// True when the chain reached ready without any earlier awaiting-credentials state, which is the
    /// shape a leftover child session produces.
    /// </summary>
    public bool DirectToReady { get; }

    /// <summary>Derives the observation from one observed state chain.</summary>
    /// <exception cref="ArgumentNullException">The chain is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The chain contains a value that is not a session state.</exception>
    public static LogonObservation Create(IReadOnlyList<SessionState> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);

        bool awaitingCredentialsObserved = false;
        bool directToReady = false;
        foreach (SessionState state in chain)
        {
            if (!Enum.IsDefined(state))
            {
                throw new ArgumentOutOfRangeException(nameof(chain), state, "The chain contains a value that is not a session state.");
            }

            if (state == SessionState.AwaitingCredentials)
            {
                awaitingCredentialsObserved = true;
            }
            else if (state == SessionState.Ready && !awaitingCredentialsObserved)
            {
                directToReady = true;
            }
        }

        SessionState[] copy = [.. chain];
        return new LogonObservation(awaitingCredentialsObserved, Array.AsReadOnly(copy), directToReady);
    }
}