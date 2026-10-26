using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;

namespace SoulComputerUse.Windows.Rdp.SessionWatch;

/// <summary>
/// Polls the connected-state numbers the remote desktop control reports until they form the ready
/// state, and refuses a chain that reached ready without the operator's own logon step.
/// </summary>
/// <remarks>
/// <para>
/// The mapping follows section 3.3 of the interface contract: 0 is disconnected, 2 is connecting, 3 is
/// awaiting-credentials and 1 is ready. Any other number is refused with
/// <see cref="ArgumentOutOfRangeException"/> instead of being guessed.
/// </para>
/// <para>
/// The logon step cannot be skipped. Ready is accepted only when the observed chain contains
/// awaiting-credentials, because a chain that jumps from connecting straight to ready is the shape a
/// leftover child session that is already logged on produces. An optional child-session logon probe can
/// postpone ready until the child session reports a logged-on operator, but it can never stand in for a
/// missing logon step.
/// </para>
/// <para>
/// There is one deadline, measured from the start of the run to the first ready and never reset or
/// paused, so time spent on the logon screen counts like any other time. There is deliberately no
/// separate timeout for the logon phase: the total budget belongs to the caller, and
/// <see cref="Timeout.InfiniteTimeSpan"/> removes the deadline entirely.
/// </para>
/// <para>
/// Every wait goes through <see cref="IWatchClock"/>; this type never sleeps a thread, so a test can
/// drive a whole timeout scenario with a fake clock. A cancelled wait surfaces as
/// <see cref="OperationCanceledException"/> exactly, never as a silent stop.
/// </para>
/// </remarks>
public sealed class SessionStateWatch
{
    /// <summary>The fixed refusal for a chain that reached ready without the logon step.</summary>
    private const string LogonBypassedMessage =
        "The session reached ready without ever entering awaiting-credentials, so the operator's logon step was bypassed, most likely because a leftover child session was already logged on and supplied the logon. The run is refused.";

    /// <summary>The fixed refusal for a chain that did not reach ready within the connect deadline.</summary>
    private const string ConnectTimeoutMessage =
        "The session did not reach ready within the connect timeout, so the watch stopped waiting. Retry the connection when the operator is able to log on.";

    private readonly IConnectedStateSource _connectedStateSource;
    private readonly Func<bool>? _isChildSessionLoggedOn;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _connectTimeout;
    private readonly bool _hasDeadline;
    private readonly IWatchClock _clock;
    private readonly Action<SessionState> _onStateChanged;

    /// <summary>Creates a watch over one connected-state port.</summary>
    /// <param name="connectedStateSource">The port that reads the client's connected-state number.</param>
    /// <param name="isChildSessionLoggedOn">
    /// An optional probe that reports whether the child session has a logged-on operator. When it is
    /// supplied, ready is postponed while it reports false; when it is null, the observed chain alone
    /// decides readiness and the logon gate still applies.
    /// </param>
    /// <param name="pollInterval">How long to wait between two reads of the connected state.</param>
    /// <param name="connectTimeout">
    /// The total budget from the start of the run to the first ready, or
    /// <see cref="Timeout.InfiniteTimeSpan"/> for no deadline. The budget is never reset, including by
    /// a stay in awaiting-credentials.
    /// </param>
    /// <param name="clock">The clock that supplies time and the cancellable wait.</param>
    /// <param name="onStateChanged">
    /// Called once for every state change, in order. The first observed state counts as one, a repeated
    /// state is not reported again, and ready is reported only after the logon gate accepts it.
    /// </param>
    /// <exception cref="ArgumentNullException">A required port or the callback is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The poll interval is not positive, or the connect timeout is neither positive nor
    /// <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    public SessionStateWatch(
        IConnectedStateSource connectedStateSource,
        Func<bool>? isChildSessionLoggedOn,
        TimeSpan pollInterval,
        TimeSpan connectTimeout,
        IWatchClock clock,
        Action<SessionState> onStateChanged)
    {
        ArgumentNullException.ThrowIfNull(connectedStateSource);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(onStateChanged);

        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollInterval),
                pollInterval,
                "The poll interval must be positive.");
        }

        bool hasDeadline = connectTimeout != Timeout.InfiniteTimeSpan;
        if (hasDeadline && connectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(connectTimeout),
                connectTimeout,
                "The connect timeout must be positive, or Timeout.InfiniteTimeSpan for no deadline.");
        }

        _connectedStateSource = connectedStateSource;
        _isChildSessionLoggedOn = isChildSessionLoggedOn;
        _pollInterval = pollInterval;
        _connectTimeout = connectTimeout;
        _hasDeadline = hasDeadline;
        _clock = clock;
        _onStateChanged = onStateChanged;
    }

    /// <summary>Creates a watch over a reader function, which is the way a hosted control is adapted.</summary>
    /// <param name="readConnectedState">Reads the client's connected-state number.</param>
    /// <param name="isChildSessionLoggedOn">
    /// An optional probe that reports whether the child session has a logged-on operator.
    /// </param>
    /// <param name="pollInterval">How long to wait between two reads of the connected state.</param>
    /// <param name="connectTimeout">The total budget to the first ready, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <param name="clock">The clock that supplies time and the cancellable wait.</param>
    /// <param name="onStateChanged">Called once for every state change, in order.</param>
    /// <exception cref="ArgumentNullException">A required port or the callback is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The poll interval or the connect timeout is out of range.</exception>
    public SessionStateWatch(
        Func<int> readConnectedState,
        Func<bool>? isChildSessionLoggedOn,
        TimeSpan pollInterval,
        TimeSpan connectTimeout,
        IWatchClock clock,
        Action<SessionState> onStateChanged)
        : this(
            new FuncConnectedStateSource(readConnectedState),
            isChildSessionLoggedOn,
            pollInterval,
            connectTimeout,
            clock,
            onStateChanged)
    {
    }

    /// <summary>
    /// Polls until the session is ready and returns what the observation proves about the logon step.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait between two polls; cancellation is never swallowed.</param>
    /// <returns>The ready result, including the observed chain and the elapsed clock time.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The client reported a connected state outside 0 through 3.</exception>
    /// <exception cref="SessionOperationException">
    /// The connection reached ready without the logon step (<see cref="SessionErrorCode.State"/>), or
    /// the connect deadline expired first (<see cref="SessionErrorCode.AgentTimeout"/>).
    /// </exception>
    public async Task<SessionStateWatchResult> RunAsync(CancellationToken cancellationToken)
    {
        TimeSpan startedAt = _clock.Elapsed;
        List<SessionState> chain = [];
        SessionState? lastReported = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDeadlineNotExpired(startedAt);

            int rawValue = _connectedStateSource.ReadConnectedState();
            SessionState state = RdpStateMapping.ToSessionState(RdpStateMapping.FromRawValue(rawValue));

            if (state == SessionState.Ready)
            {
                List<SessionState> candidate = [.. chain, state];
                LogonObservation observation = LogonObservation.Create(candidate);
                if (observation.DirectToReady)
                {
                    throw new SessionOperationException(SessionErrorCode.State, LogonBypassedMessage, retryable: false);
                }

                if (IsLogonConfirmed())
                {
                    chain.Add(state);
                    _onStateChanged(state);
                    return new SessionStateWatchResult(state, observation, _clock.Elapsed - startedAt);
                }
            }
            else if (lastReported != state)
            {
                chain.Add(state);
                lastReported = state;
                _onStateChanged(state);
            }

            TimeSpan wait = NextWait(startedAt);
            await WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsLogonConfirmed()
    {
        return _isChildSessionLoggedOn is null || _isChildSessionLoggedOn();
    }

    private void EnsureDeadlineNotExpired(TimeSpan startedAt)
    {
        if (_hasDeadline && _clock.Elapsed - startedAt >= _connectTimeout)
        {
            throw ConnectTimeout();
        }
    }

    private TimeSpan NextWait(TimeSpan startedAt)
    {
        if (!_hasDeadline)
        {
            return _pollInterval;
        }

        TimeSpan remaining = _connectTimeout - (_clock.Elapsed - startedAt);
        if (remaining <= TimeSpan.Zero)
        {
            throw ConnectTimeout();
        }

        return remaining < _pollInterval ? remaining : _pollInterval;
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await _clock.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A clock may surface cancellation as a derived type; the port documents this one, so the
            // caller sees exactly the type it was promised and the token that caused it.
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static SessionOperationException ConnectTimeout()
    {
        return new SessionOperationException(SessionErrorCode.AgentTimeout, ConnectTimeoutMessage, retryable: true);
    }
}