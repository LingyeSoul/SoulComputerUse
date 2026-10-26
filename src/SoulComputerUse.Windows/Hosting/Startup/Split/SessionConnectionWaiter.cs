using System;
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
    {
        ArgumentNullException.ThrowIfNull(readConnectedState);
        ArgumentNullException.ThrowIfNull(clock);

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
    }

    /// <summary>
    /// Whether the logon screen was reported at least once. False after a refusal that never saw it, which
    /// is the fact that makes such a refusal a bypass rather than a slow logon.
    /// </summary>
    public bool SawAwaitingCredentials { get; private set; }

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
            RdpConnectedState state = RdpStateMapping.FromRawValue(_readConnectedState());
            switch (state)
            {
                case RdpConnectedState.AwaitingLogon:
                    SawAwaitingCredentials = true;
                    break;

                case RdpConnectedState.Connected:
                    if (!SawAwaitingCredentials)
                    {
                        throw Refusal(SessionErrorCode.State, SessionStartSplitMessages.ConnectionLogonBypassed);
                    }

                    return;

                case RdpConnectedState.Idle:
                    throw Refusal(SessionErrorCode.Disconnected, SessionStartSplitMessages.ConnectionDisconnected);

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

        throw Refusal(SessionErrorCode.AgentTimeout, SessionStartSplitMessages.ConnectionTimedOut);
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

    private static SessionStartException Refusal(SessionErrorCode code, string reason)
    {
        return new SessionStartException(code, reason, []);
    }
}
