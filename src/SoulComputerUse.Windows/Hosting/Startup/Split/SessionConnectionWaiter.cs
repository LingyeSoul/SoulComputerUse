using System;
using System.Collections.Generic;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Rdp;

namespace SoulComputerUse.Windows.Hosting.Startup.Split;

/// <summary>
/// Waits for the connection to reach the connected state after the operator logged on, and refuses a
/// connection that reached it without a logon of this run behind it.
/// </summary>
/// <remarks>
/// <para>
/// The wait is the logon gate's last half. The control reports <c>3</c> while its window shows the Windows
/// logon screen and <c>1</c> once the session is live, so observing <c>2</c>, then <c>3</c>, then <c>1</c> is
/// the shortest chain that proves the operator logged on personally.
/// </para>
/// <para>
/// This machine does not produce that chain. Windows puts the logon screen of a fresh child session into
/// the session itself, which the client already reports as <c>1</c>, so the operator's password is typed
/// while the client says connected and the <c>3</c> state never appears. The gate therefore reads the
/// machine instead of the client: a session this run created that has no user name yet is showing its own
/// logon screen, and waiting is the correct answer, while a connected client with no session of this run
/// behind it was admitted by something else, most likely a leftover child session, and is refused. Once
/// Windows accepts the logon, the session's user name becomes readable and the wait ends.
/// </para>
/// <para>
/// The loop reads the state once, decides, and waits on the injected clock only when it has to look again.
/// It never sleeps on its own and never reads ambient time, so the same code runs deterministically in a
/// test that supplies a clock returning at once.
/// </para>
/// <para>
/// Every failure is an explicit exception carrying the contract code, and no message carries a path, an
/// account, a SID or a nonce. This type writes no log entries.
/// </para>
/// </remarks>
public sealed class SessionConnectionWaiter
{
    private readonly Func<int> _readConnectedState;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _timeout;
    private readonly IWaitClock _clock;

    /// <summary>Creates one waiter over the control's connected-state reader and a bounded wait.</summary>
    /// <exception cref="ArgumentNullException">The reader or the clock is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The interval or the timeout is not positive.</exception>
    public SessionConnectionWaiter(
        Func<int> readConnectedState,
        TimeSpan interval,
        TimeSpan timeout,
        IWaitClock clock)
        : this(readConnectedState, interval, timeout, clock, new NoLogonEvidence())
    {
    }

    /// <summary>
    /// Creates one waiter that also asks the machine whether a session of this run appeared and whether
    /// Windows accepted a logon for it, with the default logon grace period.
    /// </summary>
    /// <exception cref="ArgumentNullException">The reader, the evidence or the clock is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The interval or the timeout is not positive.</exception>
    public SessionConnectionWaiter(
        Func<int> readConnectedState,
        TimeSpan interval,
        TimeSpan timeout,
        IWaitClock clock,
        ILogonEvidence logonEvidence)
        : this(readConnectedState, interval, timeout, clock, logonEvidence, TimeSpan.FromSeconds(10))
    {
    }

    /// <summary>
    /// Creates one waiter with an explicit logon grace period: how long a connected client may take before
    /// the session of this run appears in the machine's session table. The grace is bounded on purpose, so
    /// a connection that nothing of ours answers is still refused rather than waited for.
    /// </summary>
    /// <exception cref="ArgumentNullException">The reader, the evidence or the clock is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The interval, the timeout or the grace is not positive.
    /// </exception>
    public SessionConnectionWaiter(
        Func<int> readConnectedState,
        TimeSpan interval,
        TimeSpan timeout,
        IWaitClock clock,
        ILogonEvidence logonEvidence,
        TimeSpan logonGrace)
    {
        ArgumentNullException.ThrowIfNull(readConnectedState);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logonEvidence);

        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "The observation interval must be positive.");
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The observation timeout must be positive.");
        }

        if (logonGrace <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(logonGrace), logonGrace, "The logon grace must be positive.");
        }

        _readConnectedState = readConnectedState;
        _interval = interval;
        _timeout = timeout;
        _clock = clock;
        _logonEvidence = logonEvidence;
        _logonGrace = logonGrace;
    }

    private readonly ILogonEvidence _logonEvidence;

    /// <summary>How long a connected client may take before the session it opened becomes visible.</summary>
    private readonly TimeSpan _logonGrace;

    /// <summary>
    /// Whether the logon screen was reported at least once. False after a refusal that never saw it, which
    /// is the fact that makes such a refusal a bypass rather than a slow logon.
    /// </summary>
    public bool SawAwaitingCredentials { get; private set; }

    /// <summary>
    /// Whether the logon was proven by the child session's own user name instead of its logon screen. True
    /// only when the session this run created became readable, which only happens after Windows accepted a
    /// logon.
    /// </summary>
    public bool SawLogonThroughSessionUser { get; private set; }

    /// <summary>
    /// Whether the operator's logon screen is up inside a session this run created. This machine's client
    /// reports that situation as connected, so the flag is the evidence that the wait is looking at a real
    /// logon in progress rather than at a session somebody else authenticated.
    /// </summary>
    public bool SawLogonScreenInCreatedSession { get; private set; }

    /// <summary>
    /// Whether the client has reported anything other than idle. The client reports idle until its transport
    /// starts, so an idle before this flag is set is the normal first observation, while an idle after it is
    /// a dropped connection.
    /// </summary>
    private bool _sawTransportStart;

    /// <summary>
    /// The connected-state numbers observed in order, with consecutive duplicates removed. The list exists
    /// for diagnostics: when a machine reports a chain the contract does not expect, the chain is the only
    /// evidence of what the client really did, and it carries no machine-specific data.
    /// </summary>
    public IReadOnlyList<int> ObservedStates => _observedStates.AsReadOnly();

    private readonly List<int> _observedStates = [];

    private void RecordObservation(int rawValue)
    {
        if (_observedStates.Count == 0 || _observedStates[^1] != rawValue)
        {
            _observedStates.Add(rawValue);
        }
    }

    /// <summary>Waits until the client reports the connected state, and proves the logon was observed first.</summary>
    /// <exception cref="SessionStartException">
    /// The client was admitted without the logon screen (<see cref="SessionErrorCode.State"/>), dropped to
    /// the disconnected state (<see cref="SessionErrorCode.Disconnected"/>), or never connected in time
    /// (<see cref="SessionErrorCode.AgentTimeout"/>).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The client reported a state it never produces.</exception>
    public void Await()
    {
        int attempts = Attempts();
        int logonGraceObservations = LogonGraceAttempts();
        int graceUsed = 0;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            int rawValue = _readConnectedState();
            RecordObservation(rawValue);
            RdpConnectedState state = RdpStateMapping.FromRawValue(rawValue);
            switch (state)
            {
                case RdpConnectedState.AwaitingLogon:
                    SawAwaitingCredentials = true;
                    break;

                case RdpConnectedState.Connected:
                    if (SawAwaitingCredentials)
                    {
                        return;
                    }

                    // The session's user name is readable only after Windows accepted a logon, so it is the
                    // strongest evidence available on a machine whose client never reports the logon screen.
                    if (_logonEvidence.HasLoggedOnUser())
                    {
                        SawLogonThroughSessionUser = true;
                        return;
                    }

                    // A session this run created with no user name yet is sitting on its own Windows logon
                    // screen: the client reports it as connected because the transport is up, and the
                    // operator is mid-logon. That is the state the contract calls awaiting-credentials, so
                    // the wait continues instead of refusing the operator's own logon.
                    if (_logonEvidence.HasCreatedSession())
                    {
                        SawLogonScreenInCreatedSession = true;
                        break;
                    }

                    // Nothing of this run answered the connection, so something else did: most likely a
                    // leftover child session that already holds a logon. The session table takes a moment to
                    // list a session Windows just created, so the verdict is repeated for a bounded grace
                    // period before it becomes a refusal.
                    graceUsed++;
                    if (graceUsed > logonGraceObservations)
                    {
                        throw Refusal(
                            SessionErrorCode.State,
                            SessionStartSplitMessages.ConnectionLogonBypassed,
                            ObservedStates);
                    }

                    break;

                case RdpConnectedState.Idle:
                    if (!_sawTransportStart)
                    {
                        // The client reports idle until its transport starts, which happens after the
                        // connect call returns. An idle before any other state therefore means "not started
                        // yet", not "the connection failed"; reading it as a drop would refuse every run.
                        break;
                    }

                    throw Refusal(SessionErrorCode.Disconnected, SessionStartSplitMessages.ConnectionDisconnected, ObservedStates);

                case RdpConnectedState.Connecting:
                    _sawTransportStart = true;
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(state),
                        state,
                        "Unknown client connected state.");
            }

            if (attempt + 1 < attempts)
            {
                _clock.Wait(_interval);
            }
        }

        throw Refusal(SessionErrorCode.AgentTimeout, SessionStartSplitMessages.ConnectionTimedOut, ObservedStates);
    }

    /// <summary>
    /// The number of observations the timeout allows. A timeout shorter than one interval still observes
    /// once, so a caller never gets a timeout without having looked at all.
    /// </summary>
    private int Attempts()
    {
        double count = Math.Ceiling(_timeout / _interval);
        return count < 1.0 ? 1 : (int)count;
    }

    /// <summary>
    /// How many connected observations may pass while the child session's user name is still unreadable. The
    /// client reports connected as soon as its transport is up, and the session's user name becomes readable
    /// a moment later; demanding it at the first observation would refuse a logon Windows already accepted.
    /// </summary>
    private int LogonGraceAttempts()
    {
        double count = Math.Ceiling(_logonGrace / _interval);
        return count < 1.0 ? 1 : (int)count;
    }

    /// <summary>
    /// The <see cref="Exception.Data"/> key under which a refusal carries the observed connected-state chain.
    /// The chain is the only evidence of what the client really did, so a refusal that names it can be
    /// diagnosed without a second run.
    /// </summary>
    public const string ObservedStatesDataKey = "SoulComputerUse.Hosting.Startup.Split.ObservedStates";

    private static SessionStartException Refusal(SessionErrorCode code, string reason, IReadOnlyList<int> observedStates)
    {
        SessionStartException refusal = new(code, reason, []);
        refusal.Data[ObservedStatesDataKey] = observedStates;
        return refusal;
    }
}
