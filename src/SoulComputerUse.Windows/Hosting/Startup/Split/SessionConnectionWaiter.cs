using System;
using System.Collections.Generic;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Rdp;

namespace SoulComputerUse.Windows.Hosting.Startup.Split;

/// <summary>
/// Waits for the connection to reach the connected state after the operator logged on, and refuses a
/// connection that reached it without ever showing the logon screen.
/// </summary>
/// <remarks>
/// <para>
/// The wait is the logon gate's last half. The control reports <c>3</c> while its window shows the Windows
/// logon screen and <c>1</c> once the session is live, so observing <c>2</c>, then <c>3</c>, then <c>1</c> is
/// the only chain that proves the operator logged on personally. A client that goes from <c>2</c> straight
/// to <c>1</c> was admitted by something else, most likely a leftover child session that answered the
/// connection, and that is refused rather than accepted: accepting it would hand over a session nobody
/// authenticated for.
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
        : this(readConnectedState, interval, timeout, clock, () => false)
    {
    }

    /// <summary>
    /// Creates one waiter that also asks whether the child session already has a logged-on user. That fact is
    /// the only remaining proof of a real logon when the client asks for the credentials itself, because the
    /// session then never paints its own logon screen and the state goes straight from connecting to
    /// connected.
    /// </summary>
    /// <exception cref="ArgumentNullException">The reader, the logon probe or the clock is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The interval or the timeout is not positive.</exception>
    public SessionConnectionWaiter(
        Func<int> readConnectedState,
        TimeSpan interval,
        TimeSpan timeout,
        IWaitClock clock,
        Func<bool> isChildSessionLoggedOn)
    {
        ArgumentNullException.ThrowIfNull(readConnectedState);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(isChildSessionLoggedOn);

        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "The observation interval must be positive.");
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The observation timeout must be positive.");
        }

        _readConnectedState = readConnectedState;
        _interval = interval;
        _timeout = timeout;
        _clock = clock;
        _isChildSessionLoggedOn = isChildSessionLoggedOn;
    }

    private readonly Func<bool> _isChildSessionLoggedOn;

    /// <summary>
    /// Whether the logon screen was reported at least once. False after a refusal that never saw it, which
    /// is the fact that makes such a refusal a bypass rather than a slow logon.
    /// </summary>
    public bool SawAwaitingCredentials { get; private set; }

    /// <summary>
    /// Whether the logon was proven by the child session's own user name instead of its logon screen. True
    /// only when the client asked for the credentials itself, in which case the session never paints its own
    /// logon screen and the state goes straight from connecting to connected.
    /// </summary>
    public bool SawLogonThroughSessionUser { get; private set; }

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
                    if (!SawAwaitingCredentials && !_isChildSessionLoggedOn())
                    {
                        throw Refusal(SessionErrorCode.State, SessionStartSplitMessages.ConnectionLogonBypassed, ObservedStates);
                    }

                    if (!SawAwaitingCredentials)
                    {
                        // The client asked for the credentials itself, so the session never painted its own
                        // logon screen; the session's own user name is then the proof that the operator
                        // authenticated, and it is recorded as the logon step the chain would otherwise miss.
                        SawLogonThroughSessionUser = true;
                    }

                    return;

                case RdpConnectedState.Idle:
                    throw Refusal(SessionErrorCode.Disconnected, SessionStartSplitMessages.ConnectionDisconnected, ObservedStates);

                case RdpConnectedState.Connecting:
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
