using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Rdp.SessionWatch;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the watch that drives the session states from the client's real connected-state
/// numbers: the mapping of section 3.3, the logon gate that refuses a connection which never showed the
/// logon step, the one overall deadline, the change callback, and the observation a caller uses to
/// decide when the agent pipe may be attached.
/// </summary>
/// <remarks>
/// Every case drives a fake clock, so the whole suite runs without a single sleep and a timeout case
/// takes microseconds instead of the timeout's wall-clock length.
/// </remarks>
[TestClass]
public sealed class RdpSessionWatchTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task ConnectingThenTheLogonScreenThenReadyIsTheAcceptedChain()
    {
        ScriptedStateSource source = new([2, 3, 1]);
        List<SessionState> reported = [];
        FakeWatchClock clock = new(cancelDuringWait: null);

        SessionStateWatch watch = CreateWatch(source, logonProbe: null, clock, reported);
        SessionStateWatchResult result = await watch.RunAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { SessionState.Connecting, SessionState.AwaitingCredentials, SessionState.Ready },
            reported,
            "The callback must follow the observed chain exactly, including the logon screen.");
        Assert.AreEqual(SessionState.Ready, result.FinalState);
        Assert.IsTrue(result.Observation.AwaitingCredentialsObserved, "The chain reported the logon screen.");
        Assert.IsFalse(result.Observation.DirectToReady, "The chain did not jump over the logon screen.");
        CollectionAssert.AreEqual(
            new[] { SessionState.Connecting, SessionState.AwaitingCredentials, SessionState.Ready },
            new List<SessionState>(result.Observation.Chain),
            "The observation must carry the chain the client really reported.");
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(20),
            result.Elapsed,
            "Two poll intervals passed between the first read and ready.");
    }

    [TestMethod]
    public async Task AConnectionThatJumpsFromConnectingToReadyFailsTheLogonGate()
    {
        ScriptedStateSource source = new([2, 1]);
        List<SessionState> reported = [];
        FakeWatchClock clock = new(cancelDuringWait: null);

        SessionStateWatch watch = CreateWatch(source, logonProbe: null, clock, reported);

        SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
            () => watch.RunAsync(CancellationToken.None));

        Assert.AreEqual(SessionErrorCode.State, error.Code, "A skipped logon step is a state refusal.");
        Assert.IsFalse(error.Retryable, "Retrying the same chain shape would skip the same logon step.");
        Assert.Contains(
            "logon",
            error.Message,
            StringComparison.OrdinalIgnoreCase,
            "The refusal must name the bypassed logon step.");
        CollectionAssert.AreEqual(
            new[] { SessionState.Connecting },
            reported,
            "Ready must never be reported when the logon gate refused it.");
    }

    [TestMethod]
    public async Task ALoggedOnProbeCannotStandInForAMissingLogonStep()
    {
        ScriptedStateSource source = new([2, 1]);
        List<SessionState> reported = [];
        FakeWatchClock clock = new(cancelDuringWait: null);
        ScriptedLogonProbe probe = new([true]);

        SessionStateWatch watch = CreateWatch(source, probe.Read, clock, reported);

        SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
            () => watch.RunAsync(CancellationToken.None));

        Assert.AreEqual(
            SessionErrorCode.State,
            error.Code,
            "A logged-on probe says the child session exists, not that the operator logged on through this connection.");
        Assert.AreEqual(0, probe.ReadCount, "The chain is refused before the probe is even consulted.");
    }

    [TestMethod]
    public async Task AnIdleClientBeforeTheConnectionStillEndsReady()
    {
        ScriptedStateSource source = new([0, 2, 3, 1]);
        List<SessionState> reported = [];
        FakeWatchClock clock = new(cancelDuringWait: null);

        SessionStateWatch watch = CreateWatch(source, logonProbe: null, clock, reported);
        SessionStateWatchResult result = await watch.RunAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[]
            {
                SessionState.Disconnected,
                SessionState.Connecting,
                SessionState.AwaitingCredentials,
                SessionState.Ready,
            },
            reported);
        Assert.IsTrue(result.Observation.AwaitingCredentialsObserved);
    }

    [TestMethod]
    public async Task ALongStayOnTheLogonScreenIsReportedOnceAndStillReachesReady()
    {
        ScriptedStateSource source = new([2, 3, 3, 3, 3, 1]);
        List<SessionState> reported = [];
        FakeWatchClock clock = new(cancelDuringWait: null);

        SessionStateWatch watch = CreateWatch(source, logonProbe: null, clock, reported);
        SessionStateWatchResult result = await watch.RunAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { SessionState.Connecting, SessionState.AwaitingCredentials, SessionState.Ready },
            reported,
            "Four reads of the logon screen are one state change, not four callbacks.");
        Assert.AreEqual(6, source.ReadCount, "The watch must keep polling while the operator logs on.");
        Assert.AreEqual(SessionState.Ready, result.FinalState);
    }

    [TestMethod]
    public async Task ARepeatedStateIsNotReportedAgain()
    {
        ScriptedStateSource source = new([2, 2, 2, 3, 3, 1]);
        List<SessionState> reported = [];
        FakeWatchClock clock = new(cancelDuringWait: null);

        SessionStateWatch watch = CreateWatch(source, logonProbe: null, clock, reported);
        await watch.RunAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { SessionState.Connecting, SessionState.AwaitingCredentials, SessionState.Ready },
            reported,
            "Only a state change may reach the callback.");
    }

    [TestMethod]
    public async Task AStateNumberTheClientNeverProducesIsRefusedNotGuessed()
    {
        ScriptedStateSource source = new([2, 7]);
        List<SessionState> reported = [];
        FakeWatchClock clock = new(cancelDuringWait: null);

        SessionStateWatch watch = CreateWatch(source, logonProbe: null, clock, reported);

        ArgumentOutOfRangeException error = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => watch.RunAsync(CancellationToken.None));

        Assert.Contains(
            "0, 1, 2 or 3",
            error.Message,
            StringComparison.Ordinal,
            "The refusal must state which numbers the client is allowed to report.");
        CollectionAssert.AreEqual(new[] { SessionState.Connecting }, reported);
    }

    [TestMethod]
    public async Task AConnectionThatNeverReachesReadyTimesOutWithARetryableCode()
    {
        ScriptedStateSource source = new([2]);
        List<SessionState> reported = [];
        FakeWatchClock clock = new(cancelDuringWait: null);
        TimeSpan shortTimeout = TimeSpan.FromMilliseconds(35);

        SessionStateWatch watch = new(
            source,
            isChildSessionLoggedOn: null,
            PollInterval,
            shortTimeout,
            clock,
            reported.Add);

        SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
            () => watch.RunAsync(CancellationToken.None));

        Assert.AreEqual(SessionErrorCode.AgentTimeout, error.Code);
        Assert.IsTrue(error.Retryable, "A connection that never became ready may be retried.");
        Assert.Contains(
            "timeout",
            error.Message,
            StringComparison.OrdinalIgnoreCase,
            "The refusal must name the deadline that expired.");
        Assert.IsLessThanOrEqualTo(
            shortTimeout,
            clock.TotalWaited,
            "The watch must not wait past its own deadline.");
        Assert.IsGreaterThan(0, source.ReadCount, "The watch must have read the connected state before giving up.");
    }

    [TestMethod]
    public async Task TimeOnTheLogonScreenDoesNotResetTheDeadline()
    {
        // The client sits on the logon screen, then reports ready after the total budget is spent. The
        // budget counts the logon screen too, because the caller owns the total and the watch never
        // pauses or restarts it.
        ScriptedStateSource source = new([3, 3, 3, 1]);
        List<SessionState> reported = [];
        FakeWatchClock clock = new(cancelDuringWait: null);

        SessionStateWatch watch = new(
            source,
            isChildSessionLoggedOn: null,
            PollInterval,
            TimeSpan.FromMilliseconds(25),
            clock,
            reported.Add);

        SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
            () => watch.RunAsync(CancellationToken.None));

        Assert.AreEqual(SessionErrorCode.AgentTimeout, error.Code);
        Assert.IsTrue(error.Retryable);
        CollectionAssert.AreEqual(
            new[] { SessionState.AwaitingCredentials },
            reported,
            "The logon screen is one state change, reported before the deadline expired.");
    }

    [TestMethod]
    public async Task AnInfiniteDeadlineLetsTheLogonScreenStayForAsLongAsTheCallerAllows()
    {
        // The caller chose no deadline, so the watch keeps polling through an arbitrarily long logon.
        // The budget for a real operator belongs to the caller, which is why this branch exists.
        ScriptedStateSource source = new([2, 3, 3, 3, 3, 3, 1]);
        List<SessionState> reported = [];
        FakeWatchClock clock = new(cancelDuringWait: null);

        SessionStateWatch watch = new(
            source,
            isChildSessionLoggedOn: null,
            TimeSpan.FromSeconds(30),
            Timeout.InfiniteTimeSpan,
            clock,
            reported.Add);

        SessionStateWatchResult result = await watch.RunAsync(CancellationToken.None);

        Assert.AreEqual(SessionState.Ready, result.FinalState);
        Assert.IsGreaterThan(
            TimeSpan.FromMinutes(1),
            result.Elapsed,
            "The watch must keep waiting past any fixed budget when the caller asked for no deadline.");
        CollectionAssert.AreEqual(
            new[] { SessionState.Connecting, SessionState.AwaitingCredentials, SessionState.Ready },
            reported);
    }

    [TestMethod]
    public async Task CancellationSurfacesAsOperationCancelled()
    {
        ScriptedStateSource source = new([2]);
        List<SessionState> reported = [];
        using CancellationTokenSource cancellation = new();
        FakeWatchClock clock = new(cancelDuringWait: cancellation);
        SessionStateWatch watch = CreateWatch(source, logonProbe: null, clock, reported);

        OperationCanceledException error = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => watch.RunAsync(cancellation.Token));

        Assert.AreEqual(
            cancellation.Token,
            error.CancellationToken,
            "The refusal must carry the token the caller cancelled, not a substitute.");
        Assert.HasCount(1, reported, "Only the first observed state may be reported before the wait is cancelled.");
    }

    [TestMethod]
    public async Task AReadyStateIsHeldBackUntilTheChildSessionReallyReportsALogon()
    {
        ScriptedStateSource source = new([2, 3, 1, 1, 1]);
        List<SessionState> reported = [];
        FakeWatchClock clock = new(cancelDuringWait: null);
        ScriptedLogonProbe probe = new([false, false, true]);

        SessionStateWatch watch = CreateWatch(source, probe.Read, clock, reported);
        SessionStateWatchResult result = await watch.RunAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { SessionState.Connecting, SessionState.AwaitingCredentials, SessionState.Ready },
            reported,
            "Ready is reported once, after the probe confirms the logon.");
        Assert.AreEqual(3, probe.ReadCount, "Ready must be re-checked on every read while the probe reports false.");
        Assert.IsTrue(result.Observation.AwaitingCredentialsObserved);
        Assert.AreEqual(SessionState.Ready, result.FinalState);
    }

    [TestMethod]
    public void TheObservationNamesAChainThatJumpedOverTheLogonScreen()
    {
        LogonObservation direct = LogonObservation.Create([SessionState.Connecting, SessionState.Ready]);
        LogonObservation throughLogon = LogonObservation.Create(
            [SessionState.Connecting, SessionState.AwaitingCredentials, SessionState.Ready]);

        Assert.IsTrue(direct.DirectToReady, "Connecting then ready never showed the logon step.");
        Assert.IsFalse(
            throughLogon.DirectToReady,
            "A chain through the logon screen is the only accepted route to ready.");
        Assert.IsTrue(throughLogon.AwaitingCredentialsObserved);
        Assert.IsFalse(direct.AwaitingCredentialsObserved);
    }

    [TestMethod]
    public void AnEmptyChainOrOneThatNeverReachesReadyIsNotAccusedOfASkip()
    {
        LogonObservation empty = LogonObservation.Create([]);
        LogonObservation stopped = LogonObservation.Create(
            [SessionState.Connecting, SessionState.Disconnected]);

        Assert.IsFalse(empty.DirectToReady);
        Assert.IsFalse(empty.AwaitingCredentialsObserved);
        Assert.IsFalse(stopped.DirectToReady, "A chain that never reached ready skipped nothing.");
    }

    [TestMethod]
    public void TheObservationRefusesAValueThatIsNotASessionState()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => LogonObservation.Create([SessionState.Connecting, (SessionState)99]));
        Assert.ThrowsExactly<ArgumentNullException>(() => LogonObservation.Create(null!));
    }

    [TestMethod]
    public void TheWatchRefusesMissingPortsAndImpossibleTimings()
    {
        FakeWatchClock clock = new(cancelDuringWait: null);
        ScriptedStateSource source = new([2]);

        Assert.ThrowsExactly<ArgumentNullException>(
            () => new SessionStateWatch(
                (IConnectedStateSource)null!,
                null,
                PollInterval,
                ConnectTimeout,
                clock,
                _ => { }));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new SessionStateWatch(source, null, PollInterval, ConnectTimeout, null!, _ => { }));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new SessionStateWatch(source, null, PollInterval, ConnectTimeout, clock, null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SessionStateWatch(source, null, TimeSpan.Zero, ConnectTimeout, clock, _ => { }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SessionStateWatch(source, null, PollInterval, TimeSpan.Zero, clock, _ => { }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SessionStateWatch(source, null, PollInterval, TimeSpan.FromSeconds(-1), clock, _ => { }));
    }

    private static SessionStateWatch CreateWatch(
        ScriptedStateSource source,
        Func<bool>? logonProbe,
        FakeWatchClock clock,
        List<SessionState> reported)
    {
        return new SessionStateWatch(source, logonProbe, PollInterval, ConnectTimeout, clock, reported.Add);
    }

    /// <summary>A connected-state port that replays a script and then repeats its last reading.</summary>
    private sealed class ScriptedStateSource : IConnectedStateSource
    {
        private readonly IReadOnlyList<int> _readings;
        private int _index;

        internal ScriptedStateSource(IReadOnlyList<int> readings)
        {
            if (readings.Count == 0)
            {
                throw new ArgumentException("A scripted source needs at least one reading.", nameof(readings));
            }

            _readings = readings;
        }

        internal int ReadCount { get; private set; }

        public int ReadConnectedState()
        {
            int index = _index < _readings.Count ? _index : _readings.Count - 1;
            _index++;
            ReadCount++;
            return _readings[index];
        }
    }

    /// <summary>A logon probe that replays a script and then repeats its last answer.</summary>
    private sealed class ScriptedLogonProbe
    {
        private readonly IReadOnlyList<bool> _answers;
        private int _index;

        internal ScriptedLogonProbe(IReadOnlyList<bool> answers)
        {
            if (answers.Count == 0)
            {
                throw new ArgumentException("A scripted probe needs at least one answer.", nameof(answers));
            }

            _answers = answers;
        }

        internal int ReadCount { get; private set; }

        internal bool Read()
        {
            int index = _index < _answers.Count ? _index : _answers.Count - 1;
            _index++;
            ReadCount++;
            return _answers[index];
        }
    }

    /// <summary>
    /// A clock with no thread and no timer: a wait advances the reading by exactly the requested delay,
    /// so a timeout case is decided by the numbers under test rather than by the machine's load. It can
    /// cancel a token during the wait to prove that cancellation travels out of the wait.
    /// </summary>
    private sealed class FakeWatchClock : IWatchClock
    {
        private readonly CancellationTokenSource? _cancelDuringWait;
        private TimeSpan _elapsed;

        internal FakeWatchClock(CancellationTokenSource? cancelDuringWait)
        {
            _cancelDuringWait = cancelDuringWait;
        }

        public TimeSpan Elapsed => _elapsed;

        internal int WaitCount { get; private set; }

        internal TimeSpan TotalWaited { get; private set; }

        public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (delay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(delay),
                    delay,
                    "The watch asked for a wait that cannot happen.");
            }

            WaitCount++;
            TotalWaited += delay;
            _cancelDuringWait?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            _elapsed += delay;
            return Task.CompletedTask;
        }
    }
}