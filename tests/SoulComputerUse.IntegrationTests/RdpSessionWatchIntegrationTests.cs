using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Rdp.SessionWatch;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// Runs the session-state watch on the real clock: the real poll delay, the real monotonic reading and
/// the real cancellation path, with a scripted connected-state source instead of a hosted control.
/// </summary>
/// <remarks>
/// The clock is the only difference from the contract suite. These cases prove that the production
/// clock drives the same chain and that a cancelled wait returns promptly, which a fake clock cannot
/// show because it has no timer to interrupt.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class RdpSessionWatchIntegrationTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    [TestMethod]
    public async Task TheRealClockDrivesTheLogonChainToReady()
    {
        ScriptedStateSource source = new([2, 3, 1]);
        List<SessionState> reported = [];
        SystemWatchClock clock = new();
        Stopwatch stopwatch = Stopwatch.StartNew();

        SessionStateWatch watch = new(
            source,
            isChildSessionLoggedOn: null,
            PollInterval,
            TimeSpan.FromSeconds(30),
            clock,
            reported.Add);

        SessionStateWatchResult result = await watch.RunAsync(CancellationToken.None);
        stopwatch.Stop();

        CollectionAssert.AreEqual(
            new[] { SessionState.Connecting, SessionState.AwaitingCredentials, SessionState.Ready },
            reported,
            "The real clock must produce the same chain as the fake one.");
        Assert.AreEqual(SessionState.Ready, result.FinalState);
        Assert.IsTrue(result.Observation.AwaitingCredentialsObserved);
        Assert.IsFalse(result.Observation.DirectToReady);
        Assert.AreEqual(3, source.ReadCount, "The script has three readings, so the watch reads exactly three times.");
        Assert.IsLessThanOrEqualTo(
            TimeSpan.FromSeconds(5),
            stopwatch.Elapsed,
            "Three readings at 20 ms apart must finish far inside five seconds.");
        Assert.IsGreaterThan(
            TimeSpan.Zero,
            result.Elapsed,
            "The reported elapsed time must be the real clock reading, not a fixed value.");
    }

    [TestMethod]
    public async Task CancellingTheRealWaitReturnsWithinOneHundredMilliseconds()
    {
        ScriptedStateSource source = new([2]);
        List<SessionState> reported = [];
        TaskCompletionSource firstState = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new();
        SystemWatchClock clock = new();

        SessionStateWatch watch = new(
            source,
            isChildSessionLoggedOn: null,
            TimeSpan.FromSeconds(5),
            Timeout.InfiniteTimeSpan,
            clock,
            state =>
            {
                reported.Add(state);
                _ = firstState.TrySetResult();
            });

        Task<SessionStateWatchResult> run = watch.RunAsync(cancellation.Token);
        await firstState.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(run.IsCompleted, "The watch must still be waiting for the next poll.");

        Stopwatch stopwatch = Stopwatch.StartNew();
        cancellation.Cancel();

        OperationCanceledException error = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(10)));
        stopwatch.Stop();

        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        CollectionAssert.AreEqual(
            new[] { SessionState.Connecting },
            reported,
            "Nothing after the cancelled wait may be reported.");
        Assert.IsLessThanOrEqualTo(
            TimeSpan.FromMilliseconds(100),
            stopwatch.Elapsed,
            "Cancelling a five-second poll delay must return at once, not after the delay runs out.");
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
}