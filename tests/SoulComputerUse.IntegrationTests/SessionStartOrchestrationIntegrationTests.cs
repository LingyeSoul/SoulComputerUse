using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.ChildSessions;
using SoulComputerUse.Windows.Hosting.Startup;
using SoulComputerUse.Windows.Ipc;
using SoulComputerUse.Windows.Native;
using SoulComputerUse.Windows.SessionLifecycle.Teardown;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// Integration tests for the startup pipeline against a real per-run single-instance lock, a real
/// ownership record under a temporary directory and fake session, elevation, agent and pipe ports.
/// </summary>
/// <remarks>
/// Hard rule for this class: it never opens a remote desktop connection, never logs a session off,
/// never prompts for elevation and never changes the machine-wide child-session switch. The real effects
/// are one private per-run lock object and one record file, both removed again inside the case.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class SessionStartOrchestrationIntegrationTests
{
    private const string TestSid = "S-1-5-21-0000000000-0000000000-0000000000-1001";
    private const int AgentProcessId = 4242;
    private const int ChildSessionId = 15;

    private static readonly DateTimeOffset HostStartTime = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AcquiredAt = new(2026, 10, 26, 8, 0, 1, TimeSpan.Zero);

    private string _root = string.Empty;
    private string _lockName = string.Empty;
    private string _sessionId = string.Empty;
    private SessionOwnerStore _store = null!;
    private Harness _harness = null!;
    private SessionStartReport? _report;
    private bool _recordMayRemain;

    [TestInitialize]
    public void CreateTemporaryState()
    {
        _root = Path.Combine(Path.GetTempPath(), "scu-session-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _lockName = @"Global\SoulComputerUse-Test-" + Guid.NewGuid().ToString("N");
        _sessionId = Guid.NewGuid().ToString("D");
        _store = new SessionOwnerStore(_root, TestSid);
        _harness = new Harness(_lockName, _sessionId, _store, _root);
        _report = null;
        _recordMayRemain = false;
    }

    [TestCleanup]
    public void DeleteTemporaryState()
    {
        _report?.LockHandle.Dispose();

        Assert.IsFalse(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "The case's own lock must be gone afterwards, otherwise a holder leaked.");

        if (_report is null && !_recordMayRemain)
        {
            Assert.IsFalse(
                File.Exists(_store.RecordPath),
                "A refused or failed start must not leave an ownership record behind.");
        }

        if (File.Exists(_store.RecordPath))
        {
            _store.Delete();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void ASuccessfulStartRunsEveryStageInTheContractOrderAndWritesAReadyRecord()
    {
        _report = Start();

        CollectionAssert.AreEqual(
            new List<string>
            {
                "lock",
                "record.read",
                "record.write:starting",
                "tier",
                "sweep",
                "gate.probe",
                "gate.policy:fDenyTSConnections",
                "gate.policy:fDenyChildConnections",
                "agent.connect",
                "session.lookup:" + AgentProcessId,
                "record.write:starting",
                "pipe.start",
                "record.write:connecting",
                "record.write:ready",
            },
            _harness.Calls,
            "The pipeline must take the lock, judge the record, sweep, gate, connect the agent, start the pipe and finish in ready.");

        CollectionAssert.AreEqual(
            new List<SessionStartStage>
            {
                SessionStartStage.Lock,
                SessionStartStage.ReadOwnerRecord,
                SessionStartStage.WriteStartingRecord,
                SessionStartStage.AuthTier,
                SessionStartStage.Sweep,
                SessionStartStage.ChildSessions,
                SessionStartStage.ConnectAgent,
                SessionStartStage.WriteSubagentSession,
                SessionStartStage.OwnerPipe,
                SessionStartStage.WriteConnecting,
                SessionStartStage.WriteReady,
            },
            Stages(_report.Steps));

        Assert.AreEqual(_sessionId, _report.SessionId);
        Assert.AreEqual(ChildSessionId, _report.SubagentWtsSessionId);
        Assert.IsFalse(_report.ChildSessionsOpenedByUs);
        Assert.IsFalse(_harness.LockHandle.Disposed, "A ready start hands the held lock to the caller.");

        SessionOwnerRecord record = ReadRecord();
        Assert.AreEqual(_sessionId, record.SessionId);
        Assert.AreEqual(Environment.ProcessId, record.HostPid);
        Assert.AreEqual(HostStartTime, record.HostProcessStartTime);
        Assert.AreEqual(ChildSessionId, record.SubagentWtsSessionId);
        Assert.IsFalse(record.ChildSessionsOpenedByUs);
        Assert.AreEqual(SessionState.Ready, record.State);
        Assert.AreEqual(AcquiredAt, record.AcquiredAt);
        Assert.AreEqual("ready", RecordStateText());
    }

    [TestMethod]
    public void TheNoneTierRefusesAndLeavesNoRecordOrLockBehind()
    {
        _harness.Tier.Tier = AuthTier.None;

        SessionStartException error = AssertStartFails(SessionErrorCode.ManualIntervention);

        CollectionAssert.AreEqual(
            new List<SessionStartStage>
            {
                SessionStartStage.Lock,
                SessionStartStage.ReadOwnerRecord,
                SessionStartStage.WriteStartingRecord,
                SessionStartStage.AuthTier,
            },
            Stages(error.Steps));

        Assert.DoesNotContain("sweep", _harness.Calls, "The none tier refuses before anything is swept.");
        Assert.AreEqual(0, _harness.AgentCalls);
        Assert.AreEqual(0, _harness.PipeCalls);
        Assert.AreEqual(0, _harness.Gate.EnableCalls);
        Assert.IsTrue(_harness.LockHandle.Disposed, "The refusal must close the lock handle.");
        Assert.IsGreaterThanOrEqualTo(1, _harness.Records.DeleteCount);
    }

    [TestMethod]
    public void AFailedSweepReleasesTheLockAndNeverReachesTheAgentOrTheOwnerPipe()
    {
        _harness.Sweep.Failure = new InvalidOperationException(
            "manual_intervention_required: a child session survived the logoff request.");

        SessionStartException error = AssertStartFails(SessionErrorCode.ManualIntervention);

        Assert.Contains(SessionStartStage.Sweep, Stages(error.Steps));
        Assert.AreEqual(0, _harness.AgentCalls, "No agent may be connected after a failed sweep.");
        Assert.AreEqual(0, _harness.PipeCalls, "No owner pipe may start after a failed sweep.");
        Assert.AreEqual(0, _harness.Gate.EnableCalls, "A failed sweep must stop before the switch is touched.");
        Assert.AreEqual(0, _harness.SessionLookupCalls);
        Assert.IsTrue(_harness.LockHandle.Disposed);
        Assert.IsGreaterThanOrEqualTo(1, _harness.Records.DeleteCount);
    }

    [TestMethod]
    public void AFailedAgentConnectionNeverStartsTheOwnerPipeAndCleansUp()
    {
        _harness.Agent.Outcome = AgentAttachOutcome.Failed(SessionErrorCode.AgentTimeout);

        SessionStartException error = AssertStartFails(SessionErrorCode.AgentTimeout);

        Assert.Contains(SessionStartStage.ConnectAgent, Stages(error.Steps));
        Assert.AreEqual(1, _harness.AgentCalls);
        Assert.AreEqual(0, _harness.PipeCalls, "The owner pipe must not start when no agent can serve calls.");
        Assert.AreEqual(0, _harness.SessionLookupCalls, "No child session may be recorded without a connection.");
        Assert.IsTrue(_harness.LockHandle.Disposed);
        Assert.IsGreaterThanOrEqualTo(1, _harness.Records.DeleteCount);
    }

    [TestMethod]
    public void ADeclinedElevationRefusesWithItsOwnCodeAndCleansUp()
    {
        _harness.Gate.Enabled = false;
        _harness.Gate.Elevation = new ElevationResult(false, ElevationFailure.Declined, 1223);

        SessionStartException error = AssertStartFails(SessionErrorCode.ChildSessionsDeclined);

        CollectionAssert.AreEqual(
            new List<SessionStartStage>
            {
                SessionStartStage.Lock,
                SessionStartStage.ReadOwnerRecord,
                SessionStartStage.WriteStartingRecord,
                SessionStartStage.AuthTier,
                SessionStartStage.Sweep,
                SessionStartStage.ChildSessions,
                SessionStartStage.EnableChildSessions,
            },
            Stages(error.Steps));
        Assert.AreEqual(1, _harness.Gate.EnableCalls);
        Assert.AreEqual(0, _harness.AgentCalls, "A declined elevation must refuse before the agent is connected.");
        Assert.IsTrue(_harness.LockHandle.Disposed);
        Assert.IsGreaterThanOrEqualTo(1, _harness.Records.DeleteCount);
    }

    [TestMethod]
    public void AnUnavailableElevationMapsToItsOwnCode()
    {
        _harness.Gate.Enabled = false;
        _harness.Gate.Elevation = new ElevationResult(false, ElevationFailure.Unavailable, 2);

        SessionStartException error = AssertStartFails(SessionErrorCode.ElevationUnavailable);

        Assert.Contains(SessionStartStage.EnableChildSessions, Stages(error.Steps));
        Assert.AreEqual(0, _harness.AgentCalls);
        Assert.IsTrue(_harness.LockHandle.Disposed);
    }

    [TestMethod]
    public void AHelperFailureMapsToChildSessionsDisabled()
    {
        _harness.Gate.Enabled = false;
        _harness.Gate.Elevation = new ElevationResult(false, ElevationFailure.HelperFailed, 3);

        SessionStartException error = AssertStartFails(SessionErrorCode.ChildSessionsDisabled);

        Assert.Contains(SessionStartStage.EnableChildSessions, Stages(error.Steps));
        Assert.AreEqual(0, _harness.AgentCalls);
        Assert.IsTrue(_harness.LockHandle.Disposed);
    }

    [TestMethod]
    public void AStartThatEnabledTheSwitchMarksTheRecordAndReportsTheFlag()
    {
        _harness.Gate.Enabled = false;

        _report = Start();

        Assert.AreEqual(1, _harness.Gate.EnableCalls, "The switch must be enabled exactly once.");
        Assert.IsTrue(_report.ChildSessionsOpenedByUs);
        Assert.IsTrue(ReadRecord().ChildSessionsOpenedByUs);
        Assert.AreEqual(SessionState.Ready, ReadRecord().State);
        Assert.Contains(SessionStartStage.RecordChildSessions, Stages(_report.Steps));
    }

    [TestMethod]
    public void ASwitchThatIsAlreadyOnIsLeftAloneAndNeverMarkedAsOurs()
    {
        _report = Start();

        Assert.AreEqual(0, _harness.Gate.EnableCalls, "An already-on switch must never be touched.");
        Assert.IsFalse(
            _report.ChildSessionsOpenedByUs,
            "A switch this process did not enable must not be marked as ours, or shutdown would turn it off.");
        Assert.IsFalse(ReadRecord().ChildSessionsOpenedByUs);
    }

    [TestMethod]
    public void ADeniedPolicyValueRefusesBeforeElevationAndNamesThePolicyValue()
    {
        _harness.Gate.Enabled = false;
        _harness.Gate.Policy[PolicyValue.RdpInbound] = PolicyState.Denied;

        SessionStartException error = AssertStartFails(SessionErrorCode.RdpInboundDenied);

        Assert.AreEqual(0, _harness.Gate.EnableCalls, "A denied policy must refuse before consent is asked for.");
        Assert.AreEqual(0, _harness.AgentCalls);
        StringAssert.Contains(error.Message, ChildSessionsPolicy.RdpInboundValueName);
        Assert.IsTrue(_harness.LockHandle.Disposed);
    }

    [TestMethod]
    public void ALeftoverRecordThatMeetsTheTakeoverCriteriaIsDeletedAndReplaced()
    {
        WriteLeftoverRecord(SessionState.Disconnected, DateTimeOffset.UtcNow.AddHours(-2));
        _harness.Records.TakeOverLeftover = true;

        _report = Start();

        CollectionAssert.AreEqual(
            new List<SessionStartStage>
            {
                SessionStartStage.Lock,
                SessionStartStage.ReadOwnerRecord,
                SessionStartStage.TakeOverRecord,
                SessionStartStage.WriteStartingRecord,
                SessionStartStage.AuthTier,
                SessionStartStage.Sweep,
                SessionStartStage.ChildSessions,
                SessionStartStage.ConnectAgent,
                SessionStartStage.WriteSubagentSession,
                SessionStartStage.OwnerPipe,
                SessionStartStage.WriteConnecting,
                SessionStartStage.WriteReady,
            },
            Stages(_report.Steps));

        SessionOwnerRecord record = ReadRecord();
        Assert.AreEqual(_sessionId, record.SessionId, "The replaced record must describe this start.");
        Assert.AreEqual(SessionState.Ready, record.State);
    }

    [TestMethod]
    public void ALeftoverRecordThatCannotBeTakenOverRefusesAndIsNeverDeleted()
    {
        WriteLeftoverRecord(SessionState.Ready, DateTimeOffset.UtcNow.AddHours(-2));
        _recordMayRemain = true;

        SessionStartException error = AssertStartFails(SessionErrorCode.LockStaleRecord);

        CollectionAssert.AreEqual(
            new List<SessionStartStage>
            {
                SessionStartStage.Lock,
                SessionStartStage.ReadOwnerRecord,
            },
            Stages(error.Steps));
        Assert.AreEqual(0, _harness.Records.DeleteCount, "A record that fails the takeover criteria is evidence, not garbage.");
        Assert.AreEqual(0, _harness.SweepCalls);
        Assert.AreEqual(0, _harness.AgentCalls);
        Assert.IsTrue(_harness.LockHandle.Disposed, "Even a stale-record refusal must release the lock it took.");
        Assert.IsTrue(File.Exists(_store.RecordPath), "The record must stay for a human to inspect.");
    }

    [TestMethod]
    public void AHeldLockRefusesWithoutReadingTheRecordOrRunningAStage()
    {
        using SingleInstanceLock blocker = SingleInstanceLock.Acquire(_lockName);

        SessionStartException error = AssertStartFails(SessionErrorCode.LockHeld);

        Assert.IsEmpty(error.Steps, "A refused lock attempt executes no stage.");
        Assert.AreEqual(0, _harness.RecordReads, "A held lock must be refused before the record is read.");
        Assert.AreEqual(0, _harness.SweepCalls);
        Assert.AreEqual(0, _harness.AgentCalls);
        Assert.IsFalse(_harness.LockHandle.Disposed, "This process never acquired the lock, so it has no handle to close.");
    }

    [TestMethod]
    public async Task AnAlreadyCancelledTokenRunsNothing()
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => _harness.CreateRunner().RunAsync(cancelled.Token));

        Assert.IsEmpty(_harness.Calls, "A cancelled start must not have run a single stage.");
        Assert.IsFalse(SingleInstanceLock.IsHeldByAnyone(_lockName));
    }

    [TestMethod]
    public void TheRealProcessSessionLookupReadsThisProcessPositiveSession()
    {
        int expected = WtsSessionProbe.GetCurrentProcessSessionId();
        int actual = new WindowsProcessSessionLookup().SessionIdFor(Environment.ProcessId);

        Assert.AreEqual(
            expected,
            actual,
            "The lookup must report the Windows session the process really runs in, because the record relies on it.");
        Assert.IsGreaterThan(0, actual);
    }

    [TestMethod]
    public void TheProcessSessionLookupRefusesANonPositiveIdOrAMissingProcess()
    {
        WindowsProcessSessionLookup lookup = new();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => lookup.SessionIdFor(0));
        Assert.ThrowsExactly<InvalidOperationException>(() => lookup.SessionIdFor(int.MaxValue));
    }

    [TestMethod]
    public void TheRecordAdapterClassifiesAbsentLiveAndUnusableRecords()
    {
        OwnerRecordStoreAdapter adapter = new(_store);

        Assert.AreEqual(OwnerRecordStatus.Absent, adapter.Read().Status, "No file must read as a normal start.");

        // A record whose owner process id is this live process cannot be taken over: the owner is here.
        _store.Write(new SessionOwnerRecord(
            _sessionId,
            Environment.ProcessId,
            HostStartTime,
            ChildSessionId,
            false,
            SessionState.Ready,
            DateTimeOffset.UtcNow));
        Assert.AreEqual(
            OwnerRecordStatus.NotTakeable,
            adapter.Read().Status,
            "A record of a live process must never be replaced, whatever its state or age.");

        // A record whose owner is gone and whose state is terminal meets the takeover criteria.
        _store.Write(new SessionOwnerRecord(
            _sessionId,
            int.MaxValue,
            HostStartTime,
            ChildSessionId,
            false,
            SessionState.Disconnected,
            DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.AreEqual(
            OwnerRecordStatus.TakeOver,
            adapter.Read().Status,
            "A gone owner with a terminal state is exactly the takeover case of section 3.");

        // A record whose owner is gone but whose state is not terminal and whose age is under a day refuses.
        _store.Write(new SessionOwnerRecord(
            _sessionId,
            int.MaxValue,
            HostStartTime,
            ChildSessionId,
            false,
            SessionState.Ready,
            DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.AreEqual(
            OwnerRecordStatus.NotTakeable,
            adapter.Read().Status,
            "A gone owner alone is not enough: the record must be terminal or more than twenty-four hours old.");

        // A record that cannot be parsed is classified as unusable instead of throwing.
        File.WriteAllText(_store.RecordPath, "{ this is not a record }");
        _recordMayRemain = true;
        Assert.AreEqual(OwnerRecordStatus.NotTakeable, adapter.Read().Status);
    }

    private SessionStartReport Start()
    {
        return _harness.CreateRunner().RunAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private SessionStartException AssertStartFails(SessionErrorCode expectedCode)
    {
        SessionStartException error = Assert.ThrowsExactly<SessionStartException>(
            () => _harness.CreateRunner().RunAsync(CancellationToken.None).GetAwaiter().GetResult());

        Assert.AreEqual(expectedCode, error.Code, error.Message);
        Assert.IsFalse(error.Message.Contains("C:\\", StringComparison.OrdinalIgnoreCase), error.Message);
        Assert.IsNotNull(error.Data[SessionStartException.StepsDataKey]);
        return error;
    }

    private SessionOwnerRecord ReadRecord()
    {
        SessionOwnerRecord? record = _store.Read();
        Assert.IsNotNull(record, "A successful start must leave a readable ownership record.");
        return record;
    }

    private string RecordStateText()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_store.RecordPath));
        return document.RootElement.GetProperty("state").GetString() ?? string.Empty;
    }

    private void WriteLeftoverRecord(SessionState state, DateTimeOffset acquiredAt)
    {
        _store.Write(new SessionOwnerRecord(
            Guid.NewGuid().ToString("D"),
            Environment.ProcessId + 1,
            HostStartTime,
            ChildSessionId,
            false,
            state,
            acquiredAt));
    }

    private static List<SessionStartStage> Stages(IReadOnlyList<SessionStartStepResult> steps)
    {
        List<SessionStartStage> stages = new(steps.Count);
        foreach (SessionStartStepResult step in steps)
        {
            stages.Add(step.Stage);
        }

        return stages;
    }

    /// <summary>The runner plus the fakes it is composed from, one per case.</summary>
    private sealed class Harness
    {
        private readonly string _sessionId;
        private readonly string _lockName;

        internal Harness(string lockName, string sessionId, SessionOwnerStore store, string temporaryRoot)
        {
            _lockName = lockName;
            _sessionId = sessionId;
            Calls = [];
            Records = new RecordingOwnerRecordStore(store, Calls);
            LockHandle = new TrackingLockHandle();
            Lock = new FakeLockAcquire(lockName, LockHandle, Calls);
            Tier = new FakeAuthTierSource(Calls);
            Sweep = new FakeSweep(Calls);
            Gate = new FakeChildSessionsGate(Calls, enabled: true);
            Agent = new FakeAgentConnector(Calls, temporaryRoot);
            Pipe = new FakeOwnerPipeHost(Calls);
            SessionLookup = new FakeProcessSessionLookup(Calls);
        }

        internal List<string> Calls { get; }

        internal FakeLockAcquire Lock { get; }

        internal TrackingLockHandle LockHandle { get; }

        internal RecordingOwnerRecordStore Records { get; }

        internal FakeAuthTierSource Tier { get; }

        internal FakeSweep Sweep { get; }

        internal FakeChildSessionsGate Gate { get; }

        internal FakeAgentConnector Agent { get; }

        internal FakeOwnerPipeHost Pipe { get; }

        internal FakeProcessSessionLookup SessionLookup { get; }

        internal int RecordReads => Records.ReadCount;

        internal int SweepCalls => Sweep.Count;

        internal int AgentCalls => Agent.Count;

        internal int PipeCalls => Pipe.Count;

        internal int SessionLookupCalls => SessionLookup.Count;

        internal SessionStartRunner CreateRunner()
        {
            return new SessionStartRunner(
                new SessionStartOptions(_sessionId, Environment.ProcessId, HostStartTime, AcquiredAt),
                Lock,
                Records,
                Tier,
                Sweep,
                Gate,
                Agent,
                Pipe,
                SessionLookup);
        }

        internal SingleInstanceLock AcquireRealLock()
        {
            return SingleInstanceLock.Acquire(_lockName);
        }
    }

    /// <summary>Wraps the real lock handle so a case can tell whether the runner closed it.</summary>
    private sealed class TrackingLockHandle : IDisposable
    {
        private IDisposable? _inner;

        internal bool Disposed { get; private set; }

        internal void Attach(IDisposable inner)
        {
            _inner = inner;
        }

        public void Dispose()
        {
            Disposed = true;
            _inner?.Dispose();
        }
    }

    private sealed class FakeLockAcquire : ISingleInstanceLockAcquire
    {
        private readonly string _lockName;
        private readonly TrackingLockHandle _handle;
        private readonly List<string> _calls;

        internal FakeLockAcquire(string lockName, TrackingLockHandle handle, List<string> calls)
        {
            _lockName = lockName;
            _handle = handle;
            _calls = calls;
        }

        public LockAttempt Acquire()
        {
            _calls.Add("lock");
            SingleInstanceLock acquired = SingleInstanceLock.Acquire(_lockName);
            if (!acquired.IsHeld)
            {
                acquired.Dispose();
                return LockAttempt.HeldElsewhere();
            }

            _handle.Attach(acquired);
            return LockAttempt.Acquired(_handle);
        }
    }

    private sealed class RecordingOwnerRecordStore : IOwnerRecordStore
    {
        private readonly SessionOwnerStore _store;
        private readonly List<string> _calls;

        internal RecordingOwnerRecordStore(SessionOwnerStore store, List<string> calls)
        {
            _store = store;
            _calls = calls;
        }

        internal int ReadCount { get; private set; }

        internal int DeleteCount { get; private set; }

        internal bool TakeOverLeftover { get; set; }

        public OwnerRecordRead Read()
        {
            ReadCount++;
            _calls.Add("record.read");

            SessionOwnerRecord? record;
            try
            {
                record = _store.Read();
            }
            catch (InvalidOperationException)
            {
                return OwnerRecordRead.NotTakeable();
            }

            if (record is null)
            {
                return OwnerRecordRead.Absent();
            }

            return TakeOverLeftover ? OwnerRecordRead.TakeOver(record) : OwnerRecordRead.NotTakeable();
        }

        public void Write(SessionOwnerRecord record)
        {
            _calls.Add("record.write:" + SessionStateNames.ToWireName(record.State));
            _store.Write(record);
        }

        public void Delete()
        {
            DeleteCount++;
            _calls.Add("record.delete");
            _store.Delete();
        }
    }

    private sealed class FakeAuthTierSource : IAuthTierSource
    {
        private readonly List<string> _calls;

        internal FakeAuthTierSource(List<string> calls)
        {
            _calls = calls;
        }

        internal AuthTier Tier { get; set; } = AuthTier.SystemCredential;

        public AuthTier Detect()
        {
            _calls.Add("tier");
            return Tier;
        }
    }

    private sealed class FakeSweep : ILeftoverSweep
    {
        private readonly List<string> _calls;

        internal FakeSweep(List<string> calls)
        {
            _calls = calls;
        }

        internal Exception? Failure { get; set; }

        internal int Count { get; private set; }

        public LeftoverSweepReport Sweep()
        {
            Count++;
            _calls.Add("sweep");
            if (Failure is not null)
            {
                throw Failure;
            }

            return new LeftoverSweepReport(Array.Empty<int>(), Array.Empty<int>());
        }
    }

    private sealed class FakeChildSessionsGate : IChildSessionsGate
    {
        private readonly List<string> _calls;
        private bool _enabled;

        internal FakeChildSessionsGate(List<string> calls, bool enabled)
        {
            _calls = calls;
            _enabled = enabled;
            Policy = new Dictionary<PolicyValue, PolicyState>
            {
                [PolicyValue.RdpInbound] = PolicyState.Allowed,
                [PolicyValue.ChildConnections] = PolicyState.Allowed,
            };
        }

        internal Dictionary<PolicyValue, PolicyState> Policy { get; }

        internal bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        internal ElevationResult? Elevation { get; set; }

        internal int EnableCalls { get; private set; }

        public bool IsEnabled()
        {
            _calls.Add("gate.probe");
            return _enabled;
        }

        public PolicyState ReadPolicy(PolicyValue value)
        {
            _calls.Add("gate.policy:" + PolicyName(value));
            return Policy[value];
        }

        public ElevationResult Enable()
        {
            EnableCalls++;
            _calls.Add("gate.enable");
            ElevationResult result = Elevation ?? new ElevationResult(true, null, 0);
            if (!result.Failed)
            {
                _enabled = true;
            }

            return result;
        }

        private static string PolicyName(PolicyValue value)
        {
            return value switch
            {
                PolicyValue.RdpInbound => ChildSessionsPolicy.RdpInboundValueName,
                PolicyValue.ChildConnections => ChildSessionsPolicy.ChildConnectionsValueName,
                _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown policy value."),
            };
        }
    }

    private sealed class FakeAgentConnector : IAgentConnector
    {
        private readonly List<string> _calls;
        private readonly string _temporaryRoot;

        internal FakeAgentConnector(List<string> calls, string temporaryRoot)
        {
            _calls = calls;
            _temporaryRoot = temporaryRoot;
        }

        internal AgentAttachOutcome? Outcome { get; set; }

        internal int Count { get; private set; }

        public AgentAttachOutcome Attach()
        {
            Count++;
            _calls.Add("agent.connect");

            // The fake agent client must never be a real pipe: a start must not connect to a live agent.
            if (!Directory.Exists(_temporaryRoot))
            {
                throw new InvalidOperationException("The case's temporary directory is gone, so the fake cannot run.");
            }

            return Outcome ?? AgentAttachOutcome.Succeeded(
                new AgentConnection(new UnavailableSessionClient(), AgentProcessId));
        }
    }

    private sealed class FakeOwnerPipeHost : IOwnerPipeHost
    {
        private readonly List<string> _calls;

        internal FakeOwnerPipeHost(List<string> calls)
        {
            _calls = calls;
        }

        internal int Count { get; private set; }

        public void Start(ISessionToolHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            Count++;
            _calls.Add("pipe.start");
        }
    }

    private sealed class FakeProcessSessionLookup : IProcessSessionLookup
    {
        private readonly List<string> _calls;

        internal FakeProcessSessionLookup(List<string> calls)
        {
            _calls = calls;
        }

        internal int Count { get; private set; }

        public int SessionIdFor(int processId)
        {
            Count++;
            _calls.Add("session.lookup:" + processId);
            return ChildSessionId;
        }
    }
}
