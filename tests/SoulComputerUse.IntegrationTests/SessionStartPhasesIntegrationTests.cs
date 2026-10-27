using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Hosting.Startup.Phases;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// Integration tests for the three-segment start against a real per-run single-instance lock and fake
/// connection, agent and observer ports.
/// </summary>
/// <remarks>
/// Hard rule for this class: it never opens a remote desktop connection, never logs a session off, never
/// prompts for elevation and never touches a value of the machine-wide child-session switch. The only
/// real effect is one private per-run lock object, which the case's own cleanup closes again; the
/// production lock name is never used.
/// <para>
/// The real lock is what makes these cases worth having: the before-connect segment takes the lock and the
/// runner has to hand it to the ready session or leave it open for the caller after a failure. A fake
/// handle could not tell whether the runner closed it at the wrong moment, while a named kernel object
/// can be probed by name.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class SessionStartPhasesIntegrationTests
{
    private string _lockName = string.Empty;
    private Harness _harness = null!;

    [TestInitialize]
    public void CreatePerRunLockName()
    {
        _lockName = @"Global\SoulComputerUse-Test-" + Guid.NewGuid().ToString("N");
        _harness = new Harness(_lockName);
    }

    [TestCleanup]
    public void ReleaseWhateverTheCaseLeft()
    {
        _harness.Release();

        Assert.IsFalse(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "The case's own lock must be gone afterwards, otherwise a holder leaked.");
    }

    [TestMethod]
    public void ACompletedStartRunsTheThreeSegmentsInOrderAndHandsTheRealLockToTheCaller()
    {
        SessionPhaseReport report = _harness.RunAsync(CancellationToken.None).GetAwaiter().GetResult();

        CollectionAssert.AreEqual(
            new List<string> { "before-connect", "open", "after-logon" },
            _harness.Calls,
            "A start runs the before-connect segment, the connection and the after-logon segment exactly once, in that order.");

        CollectionAssert.AreEqual(
            new List<SessionStartPhase>
            {
                SessionStartPhase.BeforeConnect,
                SessionStartPhase.AwaitingCredentials,
                SessionStartPhase.AfterLogon,
                SessionStartPhase.Ready,
            },
            _harness.Observer.Phases.ToList());

        Assert.IsTrue(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "A ready session keeps the machine-wide lock, so the report must hand its handle to the caller.");

        report.LockHandle.Dispose();

        Assert.IsFalse(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "Closing the handle the report handed over must release the lock again.");
    }

    [TestMethod]
    public void AFailedBeforeConnectConnectsNothingAndLeavesTheLockFree()
    {
        _harness.BeforeConnect.Failure = new InvalidOperationException("manual_intervention_required: the sweep did not finish");

        SessionPhaseException error = Assert.ThrowsExactly<SessionPhaseException>(
            () => _harness.RunAsync(CancellationToken.None).GetAwaiter().GetResult());

        Assert.AreEqual(SessionErrorCode.ManualIntervention, error.Code);
        Assert.AreEqual(SessionStartPhase.BeforeConnect, error.Phase);
        CollectionAssert.AreEqual(new List<string> { "before-connect" }, _harness.Calls);
        Assert.AreEqual(0, _harness.Opener.Count);
        Assert.AreEqual(0, _harness.AfterLogon.Count);
        Assert.IsEmpty(_harness.Observer.Phases);
        Assert.IsFalse(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "A before-connect that never took the lock must not leave one behind.");
    }

    [TestMethod]
    public void AConnectionFailureStopsTheRunAndKeepsTheLockOpenForTheCallerToClose()
    {
        _harness.TakeLock();
        _harness.Opener.Failure = new InvalidOperationException("the remote control refused to connect");

        SessionPhaseException error = Assert.ThrowsExactly<SessionPhaseException>(
            () => _harness.RunAsync(CancellationToken.None).GetAwaiter().GetResult());

        CollectionAssert.AreEqual(new List<string> { "before-connect", "open" }, _harness.Calls);
        Assert.AreEqual(0, _harness.AfterLogon.Count, "A connection that did not open must never be followed by an agent attach.");
        CollectionAssert.AreEqual(
            new List<SessionStartPhase> { SessionStartPhase.BeforeConnect, SessionStartPhase.AwaitingCredentials },
            _harness.Observer.Phases.ToList(),
            "The caption announces the logon-screen phase before the connection attempt, because the window is showing that screen for the whole call.");
        Assert.IsTrue(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "The runner must not close a lock that a before-connect segment took; the caller still owns its cleanup.");

        BeforeConnectResult carried = Assert.IsInstanceOfType<BeforeConnectResult>(
            error.Data[SessionStartPhaseRunner.BeforeConnectDataKey]);
        carried.LockHandle.Dispose();

        Assert.IsFalse(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "The lock the failure carried must release once its handle is closed.");
    }

    [TestMethod]
    public void AnAfterLogonThatIsNotReadyFailsWithNotReadyAndKeepsTheLockOpen()
    {
        _harness.TakeLock();
        _harness.AfterLogon.Result = new AfterLogonResult(
            agentAttached: true,
            ownerPipeStarted: true,
            subagentWtsSessionId: 15,
            recordReady: false);

        SessionPhaseException error = Assert.ThrowsExactly<SessionPhaseException>(
            () => _harness.RunAsync(CancellationToken.None).GetAwaiter().GetResult());

        Assert.AreEqual(SessionErrorCode.NotReady, error.Code);
        Assert.AreEqual(SessionStartPhase.AfterLogon, error.Phase);
        CollectionAssert.AreEqual(
            new List<string> { "before-connect", "open", "after-logon" },
            _harness.Calls);
        CollectionAssert.AreEqual(
            new List<SessionStartPhase>
            {
                SessionStartPhase.BeforeConnect,
                SessionStartPhase.AwaitingCredentials,
                SessionStartPhase.AfterLogon,
            },
            _harness.Observer.Phases.ToList(),
            "The ready boundary must not be announced when the record is not ready.");
        Assert.IsTrue(SingleInstanceLock.IsHeldByAnyone(_lockName));
    }

    [TestMethod]
    public async Task AnAlreadyCancelledTokenTakesNoLockAndRunsNothing()
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => _harness.RunAsync(cancelled.Token));

        Assert.IsEmpty(_harness.Calls, "A cancelled start must not run a single segment.");
        Assert.IsEmpty(_harness.Observer.Phases);
        Assert.IsFalse(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "A cancelled start acquires no lock, so the probe must find none.");
    }

    [TestMethod]
    public void AnObserverFailureDoesNotChangeWhatRan()
    {
        _harness.Observer.ThrowFor(SessionStartPhase.AwaitingCredentials);

        SessionPhaseReport report = _harness.RunAsync(CancellationToken.None).GetAwaiter().GetResult();

        CollectionAssert.AreEqual(
            new List<string> { "before-connect", "open", "after-logon" },
            _harness.Calls,
            "An observer is a reporting channel, so its failure must not change what the session did.");
        Assert.HasCount(1, report.Failures);
        Assert.AreEqual(SessionStartPhase.AwaitingCredentials, report.Failures[0].Phase);
        Assert.HasCount(4, report.Phases);
        Assert.IsTrue(SingleInstanceLock.IsHeldByAnyone(_lockName));
    }

    /// <summary>The runner plus the fakes, the real per-run lock and the call log of one case.</summary>
    private sealed class Harness
    {
        private readonly string _lockName;
        private SingleInstanceLock? _lock;

        internal Harness(string lockName)
        {
            _lockName = lockName;
            Calls = [];
            BeforeConnect = new FakeBeforeConnect(Calls, this);
            Opener = new FakeConnectionOpener(Calls);
            AfterLogon = new FakeAfterLogon(Calls);
            Observer = new RecordingObserver();
        }

        internal List<string> Calls { get; }

        internal FakeBeforeConnect BeforeConnect { get; }

        internal FakeConnectionOpener Opener { get; }

        internal FakeAfterLogon AfterLogon { get; }

        internal RecordingObserver Observer { get; }

        /// <summary>
        /// Takes the real per-run lock, which the before-connect fake hands to the runner. A second call
        /// returns the same handle, because one process holds one lock per name.
        /// </summary>
        internal SingleInstanceLock TakeLock()
        {
            if (_lock is not null)
            {
                return _lock;
            }

            _lock = SingleInstanceLock.Acquire(_lockName);
            Assert.IsTrue(_lock.IsHeld, "The case's own per-run lock must be free before it runs.");
            return _lock;
        }

        internal Task<SessionPhaseReport> RunAsync(CancellationToken cancellationToken)
        {
            SessionStartPhaseRunner runner = new(BeforeConnect, Opener, AfterLogon, Observer);
            return runner.RunAsync(cancellationToken);
        }

        public void Release()
        {
            // The runner hands the lock handle onward instead of closing it, so the case closes whatever
            // it took, whether the run failed before or after the connection.
            _lock?.Dispose();
            _lock = null;
        }
    }

    private sealed class FakeBeforeConnect : ISessionStartBeforeConnect
    {
        private readonly List<string> _calls;
        private readonly Harness _harness;

        internal FakeBeforeConnect(List<string> calls, Harness harness)
        {
            _calls = calls;
            _harness = harness;
        }

        internal Exception? Failure { get; set; }

        internal int Count { get; private set; }

        public BeforeConnectResult Execute()
        {
            Count++;
            _calls.Add("before-connect");

            if (Failure is not null)
            {
                throw Failure;
            }

            return new BeforeConnectResult(
                sweepCompleted: true,
                childSessionsOpenedByUs: false,
                alternateShellConfigured: true,
                lockHandle: _harness.TakeLock());
        }
    }

    private sealed class FakeConnectionOpener : ISessionConnectionOpener
    {
        private readonly List<string> _calls;

        internal FakeConnectionOpener(List<string> calls)
        {
            _calls = calls;
        }

        internal Exception? Failure { get; set; }

        internal int Count { get; private set; }

        public void Open()
        {
            Count++;
            _calls.Add("open");

            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }

    private sealed class FakeAfterLogon : ISessionStartAfterLogon
    {
        private readonly List<string> _calls;

        internal FakeAfterLogon(List<string> calls)
        {
            _calls = calls;
        }

        internal Exception? Failure { get; set; }

        internal AfterLogonResult Result { get; set; } =
            new(agentAttached: true, ownerPipeStarted: true, subagentWtsSessionId: 15, recordReady: true);

        internal int Count { get; private set; }

        public AfterLogonResult Execute()
        {
            Count++;
            _calls.Add("after-logon");

            if (Failure is not null)
            {
                throw Failure;
            }

            return Result;
        }
    }

    private sealed class RecordingObserver : ISessionPhaseObserver
    {
        private readonly List<SessionStartPhase> _phases = [];
        private readonly HashSet<SessionStartPhase> _throwFor = [];

        internal IReadOnlyList<SessionStartPhase> Phases => _phases;

        internal void ThrowFor(SessionStartPhase phase)
        {
            _throwFor.Add(phase);
        }

        public void OnPhaseChanged(SessionStartPhase phase)
        {
            _phases.Add(phase);

            if (_throwFor.Contains(phase))
            {
                throw new InvalidOperationException("the reporting channel refused the phase " + phase);
            }
        }
    }
}