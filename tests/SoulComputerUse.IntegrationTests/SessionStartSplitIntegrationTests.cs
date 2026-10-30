using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.ChildSessions;
using SoulComputerUse.Windows.Hosting.Startup;
using SoulComputerUse.Windows.Hosting.Startup.Phases;
using SoulComputerUse.Windows.Hosting.Startup.Split;
using SoulComputerUse.Windows.Ipc;
using SoulComputerUse.Windows.Rdp;
using SoulComputerUse.Windows.Rdp.Launch;
using SoulComputerUse.Windows.Rdp.SessionLaunch;
using SoulComputerUse.Windows.SessionLifecycle.Teardown;
using SoulComputerUse.Windows.SingleInstance;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// Integration tests for the two split segments against a real per-run single-instance lock, a real
/// ownership record in a temporary directory and fake machine, agent and pipe ports.
/// </summary>
/// <remarks>
/// Hard rule for this class: it never opens a remote desktop connection, never logs a session off, never
/// prompts for elevation, never enables the child-session switch and never touches the production lock
/// name. The only real effects are one private per-run lock object and one record file in a temporary
/// directory, both removed again inside the case.
/// <para>
/// The real record file is what makes these cases worth having: the before-connect segment writes
/// <c>starting</c> under the real lock, the after-logon segment rewrites that record twice, and a failed
/// after-logon has to leave no record while the lock the caller owns stays held. An in-memory store and a
/// fake handle could prove neither.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class SessionStartSplitIntegrationTests
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
    private IDisposable? _sessionLockHandle;

    [TestInitialize]
    public void CreateTemporaryState()
    {
        // The before-connect segment mounts the real start-up entry in this account's Run key, so a case
        // starts by removing any leftover and ends by removing its own: an entry that leaked between
        // cases would start an agent on this account's next logon.
        ChildSessionAgentBootstrap.Remove();

        _root = Path.Combine(Path.GetTempPath(), "scu-split-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _lockName = @"Global\SoulComputerUse-Test-" + Guid.NewGuid().ToString("N");
        _sessionId = Guid.NewGuid().ToString("D");
        _store = new SessionOwnerStore(_root, TestSid);
        _sessionLockHandle = null;

        Assert.IsFalse(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "The case's own per-run lock must be free before it runs, otherwise a previous case leaked it.");
    }

    [TestCleanup]
    public void DeleteTemporaryState()
    {
        _sessionLockHandle?.Dispose();
        _sessionLockHandle = null;

        Assert.IsFalse(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "The case's own lock must be gone afterwards, otherwise a holder leaked.");

        ChildSessionAgentBootstrap.Remove();
        Assert.IsNull(
            ChildSessionAgentBootstrap.Read(),
            "The start-up entry must be gone after the case, otherwise an agent would start on this account's next logon.");

        _store.Delete();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void TheTwoSegmentsTogetherLeaveAReadyRecordThatNamesTheChildSession()
    {
        Case testCase = new(_store, _lockName);
        SessionStartOptions options = CreateOptions();

        BeforeConnectResult beforeConnect = new BeforeConnectSegment(
            options,
            testCase.LockAcquire,
            testCase.Records,
            testCase.Tier,
            testCase.Sweep,
            testCase.Gate,
            testCase.Switch,
            CreateLaunchRequest(_root, _sessionId),
            new FakeControlSurface(),
            testCase.Launch).Execute();

        _sessionLockHandle = beforeConnect.LockHandle;

        AfterLogonResult afterLogon = new AfterLogonSegment(
            options,
            testCase.Records,
            testCase.Agent,
            testCase.Pipe,
            testCase.SessionLookup,
            beforeConnect).Execute();

        Assert.IsTrue(beforeConnect.SweepCompleted);
        Assert.IsFalse(beforeConnect.ChildSessionsOpenedByUs, "The case never enables the switch, so the flag stays false.");
        Assert.IsTrue(beforeConnect.AlternateShellConfigured);
        Assert.IsTrue(afterLogon.AgentAttached);
        Assert.IsTrue(afterLogon.OwnerPipeStarted);
        Assert.IsTrue(afterLogon.RecordReady);
        Assert.AreEqual(ChildSessionId, afterLogon.SubagentWtsSessionId);

        SessionOwnerRecord record = ReadRecord();
        Assert.AreEqual(_sessionId, record.SessionId);
        Assert.AreEqual(Environment.ProcessId, record.HostPid);
        Assert.AreEqual(HostStartTime, record.HostProcessStartTime);
        Assert.AreEqual(ChildSessionId, record.SubagentWtsSessionId);
        Assert.IsFalse(record.ChildSessionsOpenedByUs);
        Assert.AreEqual(SessionState.Ready, record.State);
        Assert.AreEqual(AcquiredAt, record.AcquiredAt);
        Assert.AreEqual("ready", RecordStateText(), "The record must carry the contract wire name of the state.");

        Assert.IsTrue(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "A ready session keeps the machine-wide lock, so neither segment may close the handle.");
    }

    [TestMethod]
    public void TheTwoSegmentsPassTheRecordThroughStartingThenConnectingThenReady()
    {
        Case testCase = new(_store, _lockName);
        SessionStartOptions options = CreateOptions();

        BeforeConnectResult beforeConnect = new BeforeConnectSegment(
            options,
            testCase.LockAcquire,
            testCase.Records,
            testCase.Tier,
            testCase.Sweep,
            testCase.Gate,
            testCase.Switch,
            CreateLaunchRequest(_root, _sessionId),
            new FakeControlSurface(),
            testCase.Launch).Execute();
        _sessionLockHandle = beforeConnect.LockHandle;

        SessionOwnerRecord starting = ReadRecord();

        new AfterLogonSegment(
            options,
            testCase.Records,
            testCase.Agent,
            testCase.Pipe,
            testCase.SessionLookup,
            beforeConnect).Execute();

        SessionOwnerRecord ready = ReadRecord();

        Assert.AreEqual(
            SessionState.Starting,
            starting.State,
            "The before-connect segment leaves the record in starting, because nothing may claim more before the logon.");
        Assert.AreEqual(0, starting.SubagentWtsSessionId, "No agent session exists before the operator logs on.");
        Assert.AreEqual(SessionState.Ready, ready.State);
        Assert.AreEqual(ChildSessionId, ready.SubagentWtsSessionId);
        Assert.AreEqual(_sessionId, starting.SessionId);
        Assert.AreEqual(starting.SessionId, ready.SessionId, "Every write of one start must describe the same session.");

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
                "launch.start-program",
                "launch.working-directory",
                "agent.connect",
                "session.lookup:" + AgentProcessId,
                "record.write:starting:agent-session",
                "pipe.start",
                "record.write:connecting:agent-session",
                "record.write:ready:agent-session",
            },
            testCase.Calls,
            "The two segments must run the contract order with no connection step, because these cases never connect. Actual: "
            + string.Join(" | ", testCase.Calls));
    }

    [TestMethod]
    public void AFailedAfterLogonDeletesTheRecordAndLeavesTheLockHeldForTheCaller()
    {
        Case testCase = new(_store, _lockName);
        SessionStartOptions options = CreateOptions();

        BeforeConnectResult beforeConnect = new BeforeConnectSegment(
            options,
            testCase.LockAcquire,
            testCase.Records,
            testCase.Tier,
            testCase.Sweep,
            testCase.Gate,
            testCase.Switch,
            CreateLaunchRequest(_root, _sessionId),
            new FakeControlSurface(),
            testCase.Launch).Execute();
        _sessionLockHandle = beforeConnect.LockHandle;

        testCase.Agent.Outcome = AgentAttachOutcome.Failed(SessionErrorCode.AgentTimeout);

        SessionStartException error = Assert.ThrowsExactly<SessionStartException>(
            () => new AfterLogonSegment(
                options,
                testCase.Records,
                testCase.Agent,
                testCase.Pipe,
                testCase.SessionLookup,
                beforeConnect).Execute());

        Assert.AreEqual(SessionErrorCode.AgentTimeout, error.Code, error.Message);
        Assert.IsFalse(
            File.Exists(_store.RecordPath),
            "A session that never became ready must not leave a record behind.");
        Assert.IsTrue(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "The after-logon segment never owns the lock, so its failure must leave the lock held for the caller.");
        Assert.AreEqual(0, testCase.Pipe.Count, "The owner pipe must not start when the agent refused the attachment.");
        Assert.AreEqual(0, testCase.SessionLookup.Count, "No child session may be recorded without an attachment.");
    }

    [TestMethod]
    public void ARefusedBeforeConnectLeavesNoRecordAndNoLockBehind()
    {
        Case testCase = new(_store, _lockName);
        testCase.Tier.Tier = AuthTier.None;

        SessionStartException error = Assert.ThrowsExactly<SessionStartException>(
            () => new BeforeConnectSegment(
                CreateOptions(),
                testCase.LockAcquire,
                testCase.Records,
                testCase.Tier,
                testCase.Sweep,
                testCase.Gate,
                testCase.Switch,
                CreateLaunchRequest(_root, _sessionId),
                new FakeControlSurface(),
                testCase.Launch).Execute());

        Assert.AreEqual(SessionErrorCode.ManualIntervention, error.Code, error.Message);
        Assert.IsFalse(File.Exists(_store.RecordPath), "A refused start must delete the record it wrote.");
        Assert.AreEqual(0, testCase.Agent.Count, "Nothing may attach the agent after a refused before-connect.");
        Assert.AreEqual(0, testCase.Pipe.Count);
        Assert.IsFalse(
            SingleInstanceLock.IsHeldByAnyone(_lockName),
            "The before-connect segment owns the lock, so its failure must close the handle it took.");
    }

    [TestMethod]
    public void ADeniedPolicyValueRefusesBeforeAskingForElevationAndLeavesTheLockFree()
    {
        Case testCase = new(_store, _lockName);
        testCase.Gate.Enabled = false;
        testCase.Gate.Policy[PolicyValue.RdpInbound] = PolicyState.Missing;

        SessionStartException error = Assert.ThrowsExactly<SessionStartException>(
            () => new BeforeConnectSegment(
                CreateOptions(),
                testCase.LockAcquire,
                testCase.Records,
                testCase.Tier,
                testCase.Sweep,
                testCase.Gate,
                testCase.Switch,
                CreateLaunchRequest(_root, _sessionId),
                new FakeControlSurface(),
                testCase.Launch).Execute());

        Assert.AreEqual(SessionErrorCode.RdpInboundDenied, error.Code, error.Message);
        StringAssert.Contains(error.Message, ChildSessionsPolicy.RdpInboundValueName);
        Assert.AreEqual(0, testCase.Gate.EnableCalls, "A missing policy value refuses before consent is asked for.");
        Assert.IsEmpty(testCase.Launch.Writes, "A refused start must never write the alternate-shell properties.");
        Assert.IsFalse(File.Exists(_store.RecordPath));
        Assert.IsFalse(SingleInstanceLock.IsHeldByAnyone(_lockName));
    }

    private SessionStartOptions CreateOptions()
    {
        return new SessionStartOptions(_sessionId, Environment.ProcessId, HostStartTime, AcquiredAt);
    }

    private static AgentLaunchRequest CreateLaunchRequest(string temporaryRoot, string sessionId)
    {
        // The executable is never started: the fake launch surface only records the command line, so a
        // missing file cannot turn these cases into an accidental real launch.
        string executablePath = Path.Combine(temporaryRoot, AgentExecutablePath.FileName);
        return new AgentLaunchRequest(
            executablePath,
            temporaryRoot,
            sessionId,
            new string('a', 32),
            AuthTier: null,
            GateStrength: null);
    }

    private SessionOwnerRecord ReadRecord()
    {
        SessionOwnerRecord? record = _store.Read();
        Assert.IsNotNull(record, "The case expected a readable ownership record at this point.");
        return record;
    }

    private string RecordStateText()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_store.RecordPath));
        return document.RootElement.GetProperty("state").GetString() ?? string.Empty;
    }

    /// <summary>The fake ports and the real per-run lock acquire of one case, plus its call log.</summary>
    private sealed class Case
    {
        internal Case(SessionOwnerStore store, string lockName)
        {
            Calls = [];
            Records = new RecordingOwnerRecordStore(store, Calls);
            LockAcquire = new RealLockAcquire(lockName, Calls);
            Tier = new FakeAuthTierSource(Calls);
            Sweep = new FakeSweep(Calls);
            Gate = new FakeChildSessionsGate(Calls);
            Switch = new FakeChildSessionsSwitch(Calls);
            Launch = new RecordingLaunchSurface(Calls);
            Agent = new FakeAgentConnector(Calls);
            Pipe = new FakeOwnerPipeHost(Calls);
            SessionLookup = new FakeProcessSessionLookup(Calls);
        }

        internal List<string> Calls { get; }

        internal RecordingOwnerRecordStore Records { get; }

        internal RealLockAcquire LockAcquire { get; }

        internal FakeAuthTierSource Tier { get; }

        internal FakeSweep Sweep { get; }

        internal FakeChildSessionsGate Gate { get; }

        internal FakeChildSessionsSwitch Switch { get; }

        internal RecordingLaunchSurface Launch { get; }

        internal FakeAgentConnector Agent { get; }

        internal FakeOwnerPipeHost Pipe { get; }

        internal FakeProcessSessionLookup SessionLookup { get; }
    }

    /// <summary>
    /// Takes the case's own named lock, so the segment under test handles a real kernel object while the
    /// production lock name is never used.
    /// </summary>
    private sealed class RealLockAcquire : ISingleInstanceLockAcquire
    {
        private readonly string _lockName;
        private readonly List<string> _calls;

        internal RealLockAcquire(string lockName, List<string> calls)
        {
            _lockName = lockName;
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

            return LockAttempt.Acquired(acquired);
        }
    }

    /// <summary>The real record file of the temporary directory, with the calls it received.</summary>
    private sealed class RecordingOwnerRecordStore : IOwnerRecordStore
    {
        private readonly SessionOwnerStore _store;
        private readonly List<string> _calls;

        internal RecordingOwnerRecordStore(SessionOwnerStore store, List<string> calls)
        {
            _store = store;
            _calls = calls;
        }

        public OwnerRecordRead Read()
        {
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

            // A record this case wrote belongs to a start that this case never finished, so it is treated
            // as not takeable: the test never exercises recovery through this adapter.
            return record is null ? OwnerRecordRead.Absent() : OwnerRecordRead.NotTakeable();
        }

        public void Write(SessionOwnerRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);

            string marker = record.SubagentWtsSessionId == 0
                ? string.Empty
                : ":agent-session";
            _calls.Add("record.write:" + SessionStateNames.ToWireName(record.State) + marker);
            _store.Write(record);
        }

        public void Delete()
        {
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

        public LeftoverSweepReport Sweep(int recordedChildSessionId)
        {
            _calls.Add("sweep");
            return new LeftoverSweepReport(Array.Empty<int>(), Array.Empty<int>());
        }
    }

    private sealed class FakeChildSessionsGate : IChildSessionsGate
    {
        private readonly List<string> _calls;
        private bool _enabled;

        internal FakeChildSessionsGate(List<string> calls)
        {
            _calls = calls;
            _enabled = true;
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

            // The cases never elevate for real; this branch only exists so a stray call is visible in the log.
            _enabled = true;
            return new ElevationResult(true, null, 0);
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

    /// <summary>A switch restore port that records the failed-start restore.</summary>
    private sealed class FakeChildSessionsSwitch : IChildSessionsSwitch
    {
        private readonly List<string> _calls;

        internal FakeChildSessionsSwitch(List<string> calls)
        {
            _calls = calls;
        }

        internal int RestoreCount { get; private set; }

        public void Restore()
        {
            RestoreCount++;
            _calls.Add("switch.restore");
        }
    }

    /// <summary>A launch surface that keeps the written values on the object and records each write.</summary>
    private sealed class RecordingLaunchSurface : IRdpLaunchSurface
    {
        private readonly List<string> _calls;
        private string _startProgram = string.Empty;
        private string _workingDirectory = string.Empty;

        internal RecordingLaunchSurface(List<string> calls)
        {
            _calls = calls;
        }

        internal IReadOnlyList<string> Writes => _calls.FindAll(call => call.StartsWith("launch.", StringComparison.Ordinal));

        public string StartProgram
        {
            get => _startProgram;
            set
            {
                _calls.Add("launch.start-program");
                _startProgram = value;
            }
        }

        public string WorkingDirectory
        {
            get => _workingDirectory;
            set
            {
                _calls.Add("launch.working-directory");
                _workingDirectory = value;
            }
        }
    }

    /// <summary>A hosted control stand-in; only the raw object identity reaches the launch surface.</summary>
    private sealed class FakeControlSurface : IRdpControlSurface
    {
        internal FakeControlSurface()
        {
            Ocx = new object();
        }

        public object Ocx { get; }

        public nint WindowHandle => 0;
    }

    private sealed class FakeAgentConnector : IAgentConnector
    {
        private readonly List<string> _calls;

        internal FakeAgentConnector(List<string> calls)
        {
            _calls = calls;
        }

        internal AgentAttachOutcome? Outcome { get; set; }

        internal int Count { get; private set; }

        public AgentAttachOutcome Attach()
        {
            Count++;
            _calls.Add("agent.connect");
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
